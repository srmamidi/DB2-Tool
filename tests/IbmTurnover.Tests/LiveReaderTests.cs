using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

/// <summary>
/// Runs the LIVE catalog reader and executor against a fake ADO.NET driver that answers every catalog query with
/// IBM i-shaped values (DECIMAL as decimal, TIMESTAMP as DateTime, CHAR padded, NULLs as DBNull). Fails on any
/// query it does not recognise, on a '?' count that does not match the parameters, and on a reader that asks for
/// more columns than the query selects.
/// </summary>
public class LiveReaderTests
{
    private static TurnoverOptions LiveOptions()
    {
        var o = TestEnv.Options();
        o.Mode = "Live";
        foreach (var e in o.Environments) { e.ConnectionStringName = "Db2" + e.Key; e.IfsPathsText = $"WEB=/www/orders/{e.Key.ToLowerInvariant()}/"; }
        o.TrackedAuthorizationLists.Add("ORDAUTL");
        o.TrackedJobSchedules.Add("ORD*");
        o.TrackedSystemValues.AddRange(["QDATFMT", "QMAXSIGN"]);
        o.SequenceBindings.Add(new SequenceBinding { Logical = "ORDERS", Sequence = "ORDER_SEQ", Table = "ORDER_HEADER", Column = "ORDER_ID" });
        o.Validate(_ => "Driver={IBM i Access ODBC Driver};System=fake;");
        return o;
    }

    [Fact]
    public async Task Live_reader_handles_every_catalog_query_and_value_type()
    {
        var o = LiveOptions();
        var fake = new FakeFactory();
        var provider = new Db2iCatalogProvider(fake, o);
        var catalog = new CatalogService(provider, o, TimeProvider.System);
        var snap = await catalog.LoadAsync(o.Env("QA"));

        Assert.Empty(fake.Unknown);
        var ord = snap.Libraries["ORDQA"];
        var hdr = ord.Table("ORDER_HEADER")!;
        Assert.Equal("DECIMAL(11,2)", hdr.Column("TOTAL")!.TypeSignature);
        Assert.Equal("Order total", hdr.Column("TOTAL")!.Text);
        Assert.Equal(90550, hdr.Column("ORDER_ID")!.MaxValue);
        Assert.Equal(90000, hdr.Column("ORDER_ID")!.IdentityNextValue);                // read live, so COL009 can fire on a real system
        Assert.Null(ord.Sequences.Single().MaxValue);                           // DECIMAL(31) max does not fit a long: null, no exception
        Assert.True(ord.ForeignKeys.Single().CheckPending);
        Assert.Equal("CASCADE", ord.ForeignKeys.Single().DeleteRule);
        Assert.Single(ord.Tables.Single(t => t.Name == "ORDER_LINE").Checks);
        var pl = snap.Libraries["ORDPGMQA"];
        var pgm = pl.Programs.Single(p => p.Name == "ORDENTR");
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 15, 0, 0, TimeSpan.Zero), pgm.SourceChanged); // 10:00 at UTC-5
        Assert.Contains(pgm.FileReferences, f => f.File == "ORDHDR");
        Assert.Equal(16, pgm.SourceHash.Length);
        Assert.Contains(pl.Programs, p => p.ObjectType == "*FILE" && p.Attribute == "DSPF");
        Assert.Equal("ORDPGMQA/SHPLBLR", pl.Programs.Single(p => p.ObjectType == "*CMD").CommandProgram);
        Assert.Equal("*FIFO", pl.DataQueues.Single().Sequence);
        Assert.Equal(["ORDPGMQA", "ORDQA", "QGPL"], pl.JobDescriptions.Single().LibraryList);
        Assert.Contains(ord.Authorities, a => a.User == "*OWNER" && a.Authority == "ORDOWNER");
        Assert.DoesNotContain(ord.Authorities, a => a.User == "ORDOWNER");     // owner's own row folded into *OWNER
        Assert.Equal("UIM", pl.Programs.Single(p => p.ObjectType == "*MENU").Attribute);
        Assert.Equal(["ORDPGMQA/ORDUTIL *SRVPGM *IMMED", "*LIBL/ORDTAX *MODULE"], pl.BindingDirectories.Single().Entries.Select(e => e.ToString()));
        var sys = snap.System!;
        Assert.Equal(["*PUBLIC", "ORDUSERS"], sys.AuthorizationLists.Single().Entries.Select(e => e.User));   // owner row is not a grant
        var job = sys.JobSchedules.Single();
        Assert.Equal(("*MON *WED", "22:00:00", "ORDPGMQA/ORDJOBD", "QGPL/QBATCH", "*CURRENT", "*NONE"), (job.ScheduledDays, job.ScheduledTime, job.JobDescription, job.JobQueue, job.User, job.ScheduledDate));
        Assert.True(sys.SystemValues.Single(v => v.Name == "QMAXSIGN").Numeric);
        var file = sys.IfsFiles.Single();                                       // a path outside the root is ignored
        Assert.Equal(("conf/app.json", 16), (file.RelativePath, file.Hash!.Length));
        Assert.StartsWith("CREATE OR REPLACE PROCEDURE ORDQA.SP_X", ord.Routines.Single().Definition); // SET PATH dropped
        Assert.DoesNotContain(";", ord.Routines.Single().Definition.TrimEnd());
        Assert.Equal("TIMESTAMP", hdr.Column("CREATED")!.TypeSignature);
        Assert.Equal("VARGRAPHIC(100)", hdr.Column("NOTE")!.TypeSignature);
        Assert.Equal("REAL", hdr.Column("RATE")!.TypeSignature);
        Assert.Equal("DECIMAL(31,0)", ord.Sequences.Single().DataType);
        Assert.True(ord.Table("ORDER_LINE")!.IsDds);
        Assert.Equal("SQLRPGLE", pl.Programs.Single(p => p.Name == "ORDENTR").Attribute);           // from the member's source type
        var opt = pl.Programs.Single(p => p.Name == "ORDENTR").Options!;
        Assert.Equal(("ORDERS", "V7R4M0", "*OWNER", 20, true, "*NONE", "*SQL"), (opt.ActivationGroup, opt.TargetRelease, opt.UserProfile, opt.OptimizationLevel, opt.DebugData, opt.SqlCommitment, opt.SqlNaming));
        Assert.Equal(["**free", "dcl-s"], await provider.GetSourceAsync(o.Env("QA"), "ORDSRCQA", "QRPGLESRC", "ORDENTR"));
        Assert.Equal(["*JOBCTL", "*SPLCTL"], snap.Profiles.Single(p => p.Name == "SVCORDQA").SpecialAuthorities);
        Assert.Equal("ORDPGMQA/ORDJOBD", snap.Profiles.Single(p => p.Name == "SVCORDQA").JobDescription);
        Assert.True(catalog.LastReport("QA")!.Ok);
        Assert.Contains(catalog.LastReport("QA")!.Checks, c => c.Name.StartsWith("Authorities"));
    }

    [Fact]
    public async Task Live_compare_plan_and_deploy_run_end_to_end_on_the_fake_driver()
    {
        var o = LiveOptions();
        var fake = new FakeFactory();
        var dir = TestEnv.TempDir();
        var catalog = new CatalogService(new Db2iCatalogProvider(fake, o), o, TimeProvider.System);
        var exec = new Db2DeploymentExecutor(fake, o);
        var svc = TestEnv.Service(out _, out _, out _, out _, o, exec, catalog);

        var r = await svc.CompareAsync("DEV", "QA", null);
        Assert.Empty(fake.Unknown);
        var plan = new DeploymentPlanner().Build(r, r.RecommendedSelection(), "TOV-1", "TB00000001");
        Assert.NotEmpty(plan.Steps);
        fake.Deploying = true;
        var run = await exec.RunAsync(o.Env("QA"), plan.Steps, "Live", "dave");
        Assert.True(run.Succeeded, string.Join("\n", run.Results.Where(x => x.Status == StepStatus.Failed).Select(x => x.Title + ": " + x.Message)));
        Assert.All(fake.Executed.Where(s => s.StartsWith("CALL QSYS2.QCMDEXC")), s => Assert.EndsWith("')", s));
        Assert.DoesNotContain(fake.Executed, s => s.Contains("STRJRNPF"));                  // new tables journal themselves (CPF7030 otherwise)
    }

    [Fact]
    public async Task Source_is_copied_between_partitions_over_two_connections()
    {
        var o = LiveOptions();
        var fake = new FakeFactory { MemberCount = env => env == "STAGE" ? 0 : 1, Deploying = true };
        var exec = new Db2DeploymentExecutor(fake, o);
        var run = await exec.RunAsync(o.Env("STAGE"), [CopyStep()], "Live", "dave");
        Assert.True(run.Succeeded, run.Results[0].Message);
        Assert.Contains(fake.On, x => x.Env == "STAGE" && x.Sql.Contains("ADDPFM FILE(ORDSRCSTG/QRPGLESRC) MBR(ORDENTR) SRCTYPE(SQLRPGLE)"));
        Assert.Contains(fake.On, x => x.Env == "QA" && x.Sql.Contains("QTEMP.TOVCPYSRC ORDER BY"));              // read on the source partition
        Assert.Equal(2, fake.On.Count(x => x.Env == "STAGE" && x.Sql.StartsWith("INSERT INTO QTEMP.TOVCPYDST")));  // written on the target
    }

    private static PlanStep CopyStep() => new()
    {
        Order = 1, Kind = StepKind.CopySource, Title = "copy", SourceEnvironment = "QA",
        CopyFrom = "ORDSRCQA/QRPGLESRC(ORDENTR)", CopyTo = "ORDSRCSTG/QRPGLESRC(ORDENTR)", Command = "-- copy",
    };

    [Fact]
    public async Task An_existing_target_member_is_replaced_without_ADDPFM()
    {
        var fake = new FakeFactory { Deploying = true };
        var run = await new Db2DeploymentExecutor(fake, LiveOptions()).RunAsync(LiveOptions().Env("STAGE"), [CopyStep()], "Live", "dave");
        Assert.True(run.Succeeded, run.Results[0].Message);
        Assert.DoesNotContain(fake.Executed, s => s.Contains("ADDPFM"));
    }

    [Fact]
    public async Task A_missing_source_member_is_reported_not_a_failed_library_read()
    {
        var o = LiveOptions();
        var fake = new FakeFactory { MemberMissing = true };
        var snap = await new CatalogService(new Db2iCatalogProvider(fake, o), o, TimeProvider.System).LoadAsync(o.Env("QA"));
        Assert.Equal(ProgramObject.MissingSource, snap.Libraries["ORDPGMQA"].Programs.Single(p => p.Name == "ORDENTR").SourceHash);
        Assert.DoesNotContain(fake.Executed, s => s.Contains("QTEMP.TOVSRC FOR ORDSRCQA.QRPGLESRC (ORDENTR)"));
        // Copying a member that is gone fails the step with a clear message instead of creating an empty member.
        var run = await new Db2DeploymentExecutor(new FakeFactory { MemberMissing = true, Deploying = true }, o).RunAsync(o.Env("STAGE"), [CopyStep()], "Live", "dave");
        Assert.Contains("does not exist in QA", run.Results[0].Message);
    }

    [Fact]
    public void Supplemental_groups_are_fixed_width_not_space_separated() =>
        Assert.Equal(["ORDSUPVGRP", "ADM"], Db2iCatalogProvider.FixedWidth("ORDSUPVGRPADM", 10));

    [Fact]
    public async Task Different_partitions_plan_a_copy_step_not_a_cpyf_that_cannot_see_the_source()
    {
        var r = await TestEnv.Service(out _).CompareAsync("QA", "STAGE", null);
        var pgm = r.Differences.First(d => d.Kind is ObjectKind.Program or ObjectKind.ServiceProgram && d.Change == ChangeKind.Modified);
        var plan = new DeploymentPlanner().Build(r, [pgm.Id], "T", "B");
        Assert.Contains(plan.Steps, s => s.Kind == StepKind.CopySource && s.SourceEnvironment == "QA");
        Assert.DoesNotContain(plan.Steps, s => s.Command.StartsWith("CPYF FROMFILE(ORDSRCQA"));
    }

    [Theory]
    [InlineData("TIMESTMP", 10, 6, "TIMESTAMP")]
    [InlineData("TIMESTMP", 10, 12, "TIMESTAMP(12)")]
    [InlineData("VARBIN", 64, null, "VARBINARY(64)")]
    [InlineData("FLOAT", 8, null, "DOUBLE")]
    [InlineData("DECIMAL", 11, 2, "DECIMAL(11,2)")]
    public void Catalog_type_codes_become_ddl_type_names(string code, int length, int? scale, string expected)
    {
        var (t, l, s) = Db2iCatalogProvider.SqlType(code, length, scale);
        Assert.Equal(expected, ColumnDef.TypeText(t, l, s));
    }

    [Fact]
    public void Generated_sql_keeps_only_the_create_statement() =>
        Assert.Equal("CREATE OR REPLACE VIEW A.V AS SELECT 1 FROM SYSIBM.SYSDUMMY1",
            Db2iCatalogProvider.CreateStatement("SET PATH \"QSYS\" ;\nCREATE OR REPLACE VIEW A.V AS SELECT 1 FROM SYSIBM.SYSDUMMY1 ;\n"));

    [Fact]
    public async Task A_failing_catalog_view_fails_the_whole_read_and_names_the_check()
    {
        var o = LiveOptions();
        var fake = new FakeFactory { FailOn = "DATA_QUEUE_INFO" };
        var catalog = new CatalogService(new Db2iCatalogProvider(fake, o), o, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<CatalogLoadException>(() => catalog.LoadAsync(o.Env("QA")));
        Assert.Contains(ex.Report.Checks, c => !c.Ok && c.Name.StartsWith("Data queues"));
        Assert.False(catalog.LastReport("QA")!.Ok);
    }

    [Fact]
    public async Task A_check_that_returns_rows_stops_the_deployment_before_later_steps()
    {
        var fake = new FakeFactory { CheckResult = 3 };
        var exec = new Db2DeploymentExecutor(fake, TestEnv.Options());
        PlanStep S(int n, StepKind k, string cmd) => new() { Order = n, Kind = k, Title = cmd, Command = cmd };
        var run = await exec.RunAsync(TestEnv.Options().Env("QA"), [S(1, StepKind.Check, "SELECT COUNT(*) FROM X"), S(2, StepKind.Sql, "ALTER TABLE X ADD COLUMN Y INT")], "Live", "dave");
        Assert.False(run.Succeeded);
        Assert.Equal(StepStatus.Skipped, run.Results[1].Status);
        Assert.DoesNotContain(fake.Executed, s => s.StartsWith("ALTER"));
    }
}

// ------------------------------------------------------------------------- fake driver

internal sealed class FakeFactory : IDb2ConnectionFactory
{
    public List<string> Unknown { get; } = new();
    private string lastGen = "";
    public List<string> Executed { get; } = new();
    public string? FailOn { get; init; }
    public long CheckResult { get; init; }
    /// <summary>Every statement with the environment it ran in (proves which partition a copy read from and wrote to).</summary>
    public List<(string Env, string Sql)> On { get; } = new();
    /// <summary>Rows in SYSPARTITIONSTAT for a member, per environment (0 = the member does not exist there).</summary>
    public Func<string, long> MemberCount { get; init; } = _ => 1;
    /// <summary>The member a program was compiled from is gone.</summary>
    public bool MemberMissing { get; init; }
    /// <summary>Set before running deployment steps: until then every unrecognised SELECT is a catalog query the fake does not know, and is recorded.</summary>
    public bool Deploying { get; set; }
    /// <summary>Environments whose connection fails.</summary>
    public HashSet<string> Unreachable { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<DbConnection> OpenAsync(TurnoverEnvironment env, CancellationToken ct) =>
        Unreachable.Contains(env.Key) ? Task.FromException<DbConnection>(new FakeDbException($"08001 cannot reach {env.Host}"))
                                      : Task.FromResult<DbConnection>(new FakeConnection(this, env));

    /// <summary>Rows for one statement, shaped like the IBM i Access ODBC driver returns them.</summary>
    public DataTable Respond(TurnoverEnvironment env, string sql, object?[] args)
    {
        lock (Executed) { Executed.Add(sql); On.Add((env.Key, sql)); }
        if (FailOn is not null && sql.Contains(FailOn)) throw new FakeDbException($"SQL0204 {FailOn} not found");
        var marks = sql.Count(c => c == '?') - CountInLiterals(sql);
        if (marks != args.Length) throw new FakeDbException($"{marks} parameter markers but {args.Length} parameters: {sql}");

        var sfx = env.Key switch { "DEV" => "DEV", "QA" => "QA", "STAGE" => "STG", _ => "PRD" };
        string lib = args.Length > 0 ? args[0] as string ?? "" : "";
        bool Ord() => lib == "ORD" + sfx;
        bool Pgm() => lib == "ORDPGM" + sfx || lib == "ORD" + sfx;
        var ts = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Unspecified);

        if (sql.Contains("'*LIB'")) return T(1);
        if (sql.Contains("CURRENT TIMEZONE")) return T(-50000m);
        if (sql.Contains("T.FILE_TYPE = 'D'"))
            return Ord() ? Rows(R("T", "ORDER_HEADER", "ORDHDR", "Order header", 90550L, "YES", DBNull.Value), R("P", "ORDER_LINE", "ORDLIN", "Lines", 0L, "NO", DBNull.Value)) : Rows();
        if (sql.Contains("FROM QSYS2.SYSCOLUMNS"))
            return Ord() ? Rows(
                R("ORDER_HEADER", "ORDER_ID", "ORDER_ID", 1, "BIGINT", 8, 0, "N", DBNull.Value, DBNull.Value, "YES", DBNull.Value, DBNull.Value),
                R("ORDER_HEADER", "TOTAL", "TOTAL", 2, "DECIMAL", 11, 2, "N", "0", 37, "NO", "Order total", "Order total"),
                R("ORDER_HEADER", "STATUS", "STATUS", 3, "CHAR", 2, DBNull.Value, "Y", "'OP'", 37, "NO", DBNull.Value, DBNull.Value),
                R("ORDER_HEADER", "CREATED", "CREATED", 4, "TIMESTMP", 10, 6, "N", "CURRENT_TIMESTAMP", DBNull.Value, "NO", DBNull.Value, DBNull.Value),
                R("ORDER_HEADER", "NOTE", "NOTE", 5, "VARG", 100, DBNull.Value, "Y", DBNull.Value, 1200, "NO", DBNull.Value, DBNull.Value),
                R("ORDER_HEADER", "RATE", "RATE", 6, "FLOAT", 4, DBNull.Value, "Y", DBNull.Value, DBNull.Value, "NO", DBNull.Value, DBNull.Value),
                R("ORDER_LINE", "ORDER_ID", "ORDER_ID", 1, "BIGINT", 8, 0, "N", DBNull.Value, DBNull.Value, "NO", DBNull.Value, DBNull.Value),
                R("ORDER_LINE", "QTY", "QTY", 2, "DECIMAL", 7, 0, "N", DBNull.Value, DBNull.Value, "NO", DBNull.Value, DBNull.Value),
                R("V_OPEN", "X", "X", 1, "INTEGER", 4, 0, "Y", DBNull.Value, DBNull.Value, "NO", DBNull.Value, DBNull.Value)) : Rows();
        if (sql.Contains("CONSTRAINT_TYPE IN ('PRIMARY KEY'"))
            return Ord() ? Rows(R("ORDHDR_PK", "PRIMARY KEY", "ORDER_HEADER", "ORDER_ID", "NO"), R("ORDLIN_FK", "FOREIGN KEY", "ORDER_LINE", "ORDER_ID", "YES")) : Rows();
        if (sql.Contains("FROM QSYS2.SYSREFCST"))
            return Ord() ? Rows(R("ORDLIN_FK", lib, "ORDER_HEADER", "CASCADE", "NO ACTION", "ORDER_ID")) : Rows();
        if (sql.Contains("QSYS2.SYSCHKCST")) return Ord() ? Rows(R("ORDLIN_QTY_CK", "ORDER_LINE", "QTY > 0")) : Rows();
        if (sql.Contains("FROM QSYS2.SYSINDEXES")) return Ord() ? Rows(R("ORDHDR_IX", "ORDHDRIX", "ORDER_HEADER", "D", "STATUS", "D")) : Rows();
        if (sql.Contains("FROM QSYS2.SYSSEQUENCES"))
            return Ord() ? Rows(R("ORDER_SEQ", "DECIMAL", "1", "1", "1", "9999999999999999999999999999999", "NO", 20, 31)) : Rows();
        if (sql.Contains("TABLE_TYPE = 'A'")) return Ord() ? Rows(R("ORDHDR_A", lib, "ORDER_HEADER", DBNull.Value)) : Rows();
        if (sql.Contains("FROM QSYS2.SYSROUTINES")) return Ord() ? Rows(R("SP_X", "SP_X", "PROCEDURE", DBNull.Value, DBNull.Value)) : Rows();
        if (sql.Contains("FROM QSYS2.SYSPARMS")) return Rows(R("IN", "P_ID", "BIGINT"));
        if (sql.Contains("GENERATE_SQL")) { lastGen = $"{args[1]}.{args[0]}"; return Rows(); }
        if (sql.Contains("QTEMP.Q_GENSQL"))
            return Rows(R("SET PATH \"QSYS\",\"QSYS2\" ;"), R($"CREATE OR REPLACE PROCEDURE {lastGen} (IN P_ID BIGINT)"), R("  LANGUAGE SQL"), R("  BEGIN END ;"));
        if (sql.Contains("SOURCE_TYPE") && sql.Contains("SYSPARTITIONSTAT")) return MemberMissing ? Rows() : T("SQLRPGLE");
        if (sql.Contains("QTEMP.TOVCPYSRC")) return Rows(R(1.00m, 260901m, "     H DFTACTGRP(*NO)"), R(2.00m, 260901m, "     C                   RETURN"));
        if (sql.Contains("FROM QSYS2.SYSVIEWS")) return Ord() ? Rows(R("V_OPEN", "V_OPEN")) : Rows();
        if (sql.Contains("FROM QSYS2.SYSVIEWDEP")) return Rows(R(lib, "ORDER_HEADER"));
        if (sql.Contains("FROM QSYS2.SYSTRIGGERS")) return Rows();
        if (sql.Contains("FROM QSYS2.BOUND_MODULE_INFO"))
            return Pgm() && lib.StartsWith("ORDPGM") ? Rows(R("ORDENTR", "*PGM", "RPGLE", "ORDSRC" + sfx, "QRPGLESRC", "ORDENTR", ts, ts.AddMinutes(5),
                20, "*YES", "*NONE", "*SQL", DBNull.Value, "ORD" + sfx)) : Rows();
        if (sql.Contains("FROM QSYS2.PROGRAM_INFO"))
            return Rows(R("ORDENTR", "*PGM", "ORDERS", "V7R4M0", "*OWNER", "*NO", DBNull.Value, 1, DBNull.Value, DBNull.Value, DBNull.Value));
        if (sql.Contains("COUNT(*) FROM QSYS2.SYSPARTITIONSTAT")) return T(MemberCount(env.Key));
        if (sql.Contains("QTEMP.TOVSRCTXT")) return Rows(R("**free   "), R(new byte[] { 0x84, 0x83, 0x93, 0x60, 0xA2 }));
        if (sql.Contains("FROM QSYS2.BOUND_SRVPGM_INFO")) return Rows(R("ORDENTR", "*PGM", "ORDPGM" + sfx, "ORDUTIL"));
        if (sql.Contains("'*FILE *CMD *PGM"))
            return lib.StartsWith("ORDPGM") ? Rows(R("ORDENTD", "*FILE", "DSPF", "ORDSRC" + sfx, "QDDSSRC", "ORDENTD", ts, ts, "Screens"),
                R("SHPLBL", "*CMD", "CMD", "ORDSRC" + sfx, "QCMDSRC", "SHPLBL", DBNull.Value, ts, ""),
                R("ORDMNU", "*MENU", "UIM", "ORDSRC" + sfx, "QMNUSRC", "ORDMNU", ts, ts, "Order menu")) : Rows();
        if (sql.Contains("'*BNDDIR'")) return lib.StartsWith("ORDPGM") ? Rows(R("ORDBND", "Order binding directory")) : Rows();
        if (sql.Contains("FROM QSYS2.BINDING_DIRECTORY_INFO")) return Rows(R("ORDBND", "ORDPGM" + sfx, "ORDUTIL", "*SRVPGM", "*IMMED"), R("ORDBND", "*LIBL", "ORDTAX", "*MODULE", DBNull.Value));
        if (sql.Contains("FROM QSYS2.AUTHORIZATION_LIST_USER_INFO"))
            return Rows(R("ORDAUTL", "QSECOFR", "*ALL", "QSECOFR"), R("ORDAUTL", "*PUBLIC", "*EXCLUDE", "QSECOFR"), R("ORDAUTL", "ORDUSERS", "*USE", "QSECOFR"));
        if (sql.Contains("FROM QSYS2.SCHEDULED_JOB_INFO"))
            return Rows(R("ORDNIGHTLY", 12, "SCHEDULED", "*WEEKLY", "SCHEDULED_DAYS", DBNull.Value, "*MON,*WED", "22.00.00", DBNull.Value, $"CALL PGM(ORDPGM{sfx}/ORDRPT)",
                "ORDPGM" + sfx, "ORDJOBD", "QGPL", "QBATCH", DBNull.Value, "Nightly"));
        if (sql.Contains("FROM QSYS2.SYSTEM_VALUE_INFO")) return Rows(R("QDATFMT", DBNull.Value, "MDY"), R("QMAXSIGN", "3", DBNull.Value));
        if (sql.Contains("IFS_OBJECT_STATISTICS(START_PATH_NAME"))
            return Rows(R($"/www/orders/{env.Key.ToLowerInvariant()}/conf/app.json", 5L, ts, 1208), R("/elsewhere/x.txt", 1L, ts, 1208));
        if (sql.Contains("IFS_READ_BINARY")) return Rows(R(new byte[] { 1, 2, 3 }), R(new byte[] { 4, 5 }));
        if (sql.StartsWith("CALL QSYS2.IFS_WRITE_BINARY")) return Rows();
        if (sql.Contains("FROM QSYS2.COMMAND_INFO")) return Rows(R("SHPLBL", "ORDPGM" + sfx, "SHPLBLR"));
        if (sql.Contains("DSPPGMREF") || sql.Contains("CREATE OR REPLACE ALIAS QTEMP")) return Rows();
        if (sql.Contains("QTEMP.TOVPGMREF")) return Rows(R("ORDENTR", "ORDHDR", "*LIBL", "3A1B2C3D4E5F6"), R("ORDENTR", "ORDENTD", "*LIBL", "X"));
        if (sql.Contains("QTEMP.TOVSRC")) return Rows(R("     H DFTACTGRP(*NO)   "), R("     C                   RETURN"));
        if (sql.Contains("FROM QSYS2.DATA_AREA_INFO")) return lib.StartsWith("ORDPGM") ? Rows(R("ORDCTL", "*CHAR", 50, DBNull.Value, $"LIB=ORD{sfx}", "Control")) : Rows();
        if (sql.Contains("FROM QSYS2.DATA_QUEUE_INFO")) return lib.StartsWith("ORDPGM") ? Rows(R("ORDQ", 128, "FIFO", DBNull.Value, "NO", "Orders", 0L)) : Rows();
        if (sql.Contains("FROM QSYS2.JOB_DESCRIPTION_INFO"))
            return lib.StartsWith("ORDPGM") ? Rows(R("ORDJOBD", $"ORDPGM{sfx}   ORD{sfx}      QGPL      ", "SVCORD" + sfx, "QGPL", "QBATCH", "Jobs")) : Rows();
        if (sql.Contains("FROM QSYS2.MESSAGE_FILE_DATA")) return lib.StartsWith("ORDPGM") ? Rows(R("ORDMSGF", "ORD0001", "Order &1 not found.", DBNull.Value, 30)) : Rows();
        if (sql.Contains("FROM QSYS2.OBJECT_PRIVILEGES"))
            return Ord() ? Rows(R("ORDHDR", "*FILE", "ORDOWNER", "*ALL", "ORDOWNER"), R("ORDHDR", "*FILE", "*PUBLIC", "*EXCLUDE", "ORDOWNER"),
                R("ORDHDR", "*FILE", "SVCORD" + sfx, "*CHANGE", "ORDOWNER")) : Rows();
        if (sql.Contains("QSYS2.SYSPSTAT")) return T("90000");                                       // identity generator behind the data
        if (sql.Contains("CAST(MAX(")) return T("90550");
        if (sql.Contains("FROM QSYS2.USER_INFO"))
            return Rows(R("SVCORD" + sfx, "*ENABLED", "*USER", "*JOBCTL   *SPLCTL", "ORDUSERS", DBNull.Value, "ORD" + sfx, DBNull.Value, "*NONE",
                "*LIBL", "MAIN", "*YES", "ORDPGM" + sfx, "ORDJOBD", "YES", "Order service account"));
        // Deployment statements and checks. A SELECT the fake does not know, during a catalog read, is a finding.
        if (sql.StartsWith("SELECT "))
        {
            if (!Deploying) lock (Unknown) Unknown.Add(sql);
            return T(CheckResult);
        }
        if (sql.StartsWith("CALL QSYS2.QCMDEXC") || sql.StartsWith("ALTER ") || sql.StartsWith("CREATE ") || sql.StartsWith("DROP ") ||
            sql.StartsWith("LABEL ") || sql.StartsWith("UPDATE ") || sql.StartsWith("RENAME ") || sql.StartsWith("DELETE ") || sql.StartsWith("INSERT ")) return Rows();
        lock (Unknown) Unknown.Add(sql);
        return Rows();
    }

    private static int CountInLiterals(string sql)
    {
        var n = 0; var inside = false;
        foreach (var c in sql) { if (c == '\'') inside = !inside; else if (inside && c == '?') n++; }
        return n;
    }

    private static object[] R(params object[] v) => v;

    private static DataTable Rows(params object[][] rows)
    {
        var t = new DataTable();
        var width = rows.Length == 0 ? 40 : rows.Max(r => r.Length);
        for (var i = 0; i < width; i++) t.Columns.Add("C" + i, typeof(object));
        foreach (var r in rows) t.Rows.Add(r.Concat(Enumerable.Repeat<object>(DBNull.Value, width - r.Length)).ToArray());
        t.ExtendedProperties["width"] = rows.Length == 0 ? -1 : rows[0].Length;
        return t;
    }

    private static DataTable T(object scalar) => Rows(R(scalar));
}

internal sealed class FakeDbException(string message) : DbException(message);

internal sealed class FakeConnection(FakeFactory factory, TurnoverEnvironment env) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;
    [AllowNull] public override string ConnectionString { get; set; } = "fake";
    public override string Database => "fake";
    public override string DataSource => env.Host;
    public override string ServerVersion => "07.05.0000";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeCommand(this, factory, env);
}

internal sealed class FakeCommand(FakeConnection con, FakeFactory factory, TurnoverEnvironment env) : DbCommand
{
    private readonly DbParameterCollection _params = new System.Data.Odbc.OdbcCommand().Parameters;
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get => con; set { } }
    protected override DbParameterCollection DbParameterCollection => _params;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new System.Data.Odbc.OdbcParameter();

    private DataTable Run()
    {
        if (con.State != ConnectionState.Open) throw new InvalidOperationException("Connection is closed.");
        return factory.Respond(env, CommandText, _params.Cast<DbParameter>().Select(p => p.Value).ToArray());
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new CheckedReader(Run(), CommandText);
    public override int ExecuteNonQuery() { Run(); return 0; }
    public override object? ExecuteScalar() { var t = Run(); return t.Rows.Count == 0 ? null : t.Rows[0][0]; }
}

/// <summary>Fails when code reads a column index the SELECT list does not have.</summary>
internal sealed class CheckedReader(DataTable table, string sql) : DbDataReader
{
    private readonly DataTableReader _r = table.CreateDataReader();
    private readonly int _selected = SelectWidth(sql);

    private static int SelectWidth(string sql)
    {
        var s = sql.TrimStart();
        if (!s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        var from = s.IndexOf(" FROM ", StringComparison.OrdinalIgnoreCase);
        var list = from < 0 ? s[6..] : s[6..from];
        int depth = 0, n = 1;
        foreach (var c in list) { if (c == '(') depth++; else if (c == ')') depth--; else if (c == ',' && depth == 0) n++; }
        return n;
    }

    private object Get(int i)
    {
        if (i >= _selected) throw new IndexOutOfRangeException($"Column {i} read but the query selects {_selected}: {sql}");
        return _r.GetValue(i);
    }

    public override object this[int ordinal] => Get(ordinal);
    public override object this[string name] => throw new NotSupportedException();
    public override int Depth => 0;
    public override int FieldCount => Math.Min(_selected, _r.FieldCount);
    public override bool HasRows => _r.HasRows;
    public override bool IsClosed => _r.IsClosed;
    public override int RecordsAffected => -1;
    public override bool GetBoolean(int o) => (bool)Get(o);
    public override byte GetByte(int o) => (byte)Get(o);
    public override long GetBytes(int o, long d, byte[]? b, int bo, int l) => throw new NotSupportedException();
    public override char GetChar(int o) => (char)Get(o);
    public override long GetChars(int o, long d, char[]? b, int bo, int l) => throw new NotSupportedException();
    public override string GetDataTypeName(int o) => "object";
    public override DateTime GetDateTime(int o) => (DateTime)Get(o);
    public override decimal GetDecimal(int o) => (decimal)Get(o);
    public override double GetDouble(int o) => (double)Get(o);
    public override Type GetFieldType(int o) => typeof(object);
    public override float GetFloat(int o) => (float)Get(o);
    public override Guid GetGuid(int o) => (Guid)Get(o);
    public override short GetInt16(int o) => (short)Get(o);
    public override int GetInt32(int o) => (int)Get(o);
    public override long GetInt64(int o) => (long)Get(o);
    public override string GetName(int o) => "C" + o;
    public override int GetOrdinal(string name) => throw new NotSupportedException();
    public override string GetString(int o) => (string)Get(o);
    public override object GetValue(int o) => Get(o);
    public override int GetValues(object[] values) => _r.GetValues(values);
    public override bool IsDBNull(int o) => Get(o) is DBNull;
    public override bool NextResult() => _r.NextResult();
    public override bool Read() => _r.Read();
    public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)_r).GetEnumerator();
}

public class ConnectionStringTests
{
    [Fact]
    public void Sql_naming_and_no_commitment_control_are_enforced()
    {
        var s = OdbcConnectionFactory.Normalize("Driver={IBM i Access ODBC Driver};System=QA;", "QA");
        Assert.Contains("NAM=0", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CMT=0", s, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<TurnoverException>(() => OdbcConnectionFactory.Normalize("Driver={x};System=QA;NAM=1;", "QA"));
        Assert.Throws<TurnoverException>(() => OdbcConnectionFactory.Normalize("Driver={x};System=QA;CommitMode=2;", "QA"));
    }
}
