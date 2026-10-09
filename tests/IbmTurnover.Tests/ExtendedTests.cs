using System.Diagnostics;
using IbmTurnover.Core;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

public class ObjectCoverageTests
{
    private static async Task<ComparisonResult> DevQa() => await TestEnv.Service(out _).CompareAsync("DEV", "QA", null);
    private static Difference Diff(ComparisonResult r, ObjectKind k, string name, string? parent = null) =>
        r.Differences.SingleOrDefault(d => d.Kind == k && d.Name == name && (parent is null || d.Parent == parent))
        ?? throw new Xunit.Sdk.XunitException($"No {k} {parent}.{name}. Have: {string.Join(", ", r.Differences.Where(d => d.Kind == k).Select(d => d.DisplayName))}");
    private static bool Has(Difference d, string code) => d.Findings.Any(f => f.Code == code);

    [Fact]
    public async Task Service_accounts_are_compared_through_the_profile_map()
    {
        var r = await DevQa();
        var svc = Diff(r, ObjectKind.UserProfile, "SVCORDQA");
        Assert.True(Has(svc, "USR002"));                                         // *JOBCTL granted in Dev only
        Assert.Contains(svc.Changes, c => c.Attribute == "Special authorities" && c.After == "*JOBCTL");
        Assert.Equal("ORDPGMQA/ORDJOBD", ((UserProfileDef)svc.Source!).JobDescription); // library remapped
        var ship = Diff(r, ObjectKind.UserProfile, "SVCSHPQA");
        Assert.Equal(ChangeKind.Added, ship.Change);
        Assert.Contains("PASSWORD(*NONE)", new DeploymentPlanner().Build(r, [ship.Id], "T", "B").Steps.Single(s => s.Title.Contains("SVCSHPQA")).Command);
    }

    [Fact]
    public async Task Authorities_owner_and_public_access_are_checked()
    {
        var r = await DevQa();
        var hdr = Diff(r, ObjectKind.Authority, "ORDHDR *FILE");
        Assert.Contains(hdr.Findings, f => f.Code == "AUT001" && f.Severity == Severity.Warning); // *PUBLIC *EXCLUDE → *CHANGE
        var aud = Diff(r, ObjectKind.Authority, "ORDAUD *FILE");
        Assert.True(Has(aud, "AUT003"));                                         // SVCORDPRD is PROD's account
        Assert.True(aud.HasOpenBlocker);
    }

    [Fact]
    public async Task Data_areas_remap_library_values_and_mask_secrets()
    {
        var r = await DevQa();
        Assert.DoesNotContain(r.Differences, d => d.Kind == ObjectKind.DataArea && d.Name == "ORDCTL"); // LIB=ORDDEV → LIB=ORDQA: equal after remap
        var pwd = Diff(r, ObjectKind.DataArea, "ORDFTPPWD");
        Assert.Equal(ChangeKind.Drift, pwd.Change);
        Assert.False(pwd.DefaultSelected);
        Assert.DoesNotContain("pwd=", pwd.SourceDdl);
        Assert.DoesNotContain(pwd.Changes, c => (c.After ?? "").Contains("pwd=") || (c.Before ?? "").Contains("pwd="));
        var plan = new DeploymentPlanner().Build(r, [pwd.Id], "T", "B");
        Assert.DoesNotContain(plan.Steps, s => s.Command.Contains("pwd=") || (s.Rollback ?? "").Contains("pwd="));
        Assert.True(Has(Diff(r, ObjectKind.DataArea, "ORDLIMIT"), "DTA001"));
    }

    [Fact]
    public async Task Data_queue_job_description_and_messages()
    {
        var r = await DevQa();
        var q = Diff(r, ObjectKind.DataQueue, "ORDQ");
        Assert.True(Has(q, "DTQ001"));
        Assert.Contains(q.PreChecks, p => p.Sql.Contains("DATA_QUEUE_INFO"));
        var jobd = Diff(r, ObjectKind.JobDescription, "ORDJOBD");
        Assert.True(Has(jobd, "JOBD003"));                                       // SMAMIDI (personal library) in the library list
        Assert.Contains("ORDPGMQA", ((JobDescriptionDef)jobd.Source!).LibraryList);
        Assert.Equal(ChangeKind.Added, Diff(r, ObjectKind.Message, "ORD0002", "ORDMSGF").Change);
        Assert.Equal(ChangeKind.Modified, Diff(r, ObjectKind.Message, "ORD0001", "ORDMSGF").Change);
    }

    [Fact]
    public async Task Display_files_checks_aliases_text_and_check_pending()
    {
        var r = await DevQa();
        var dspf = Diff(r, ObjectKind.DdsFile, "ORDENTD");
        var plan = new DeploymentPlanner().Build(r, [dspf.Id], "T", "B");
        Assert.Contains(plan.Steps, s => s.Command == "DLTF FILE(ORDPGMQA/ORDENTD)");
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CRTDSPF FILE(ORDPGMQA/ORDENTD)"));
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("GRTOBJAUT OBJ(ORDPGMQA/ORDENTD)"));  // private authorities re-applied
        Assert.Contains(Diff(r, ObjectKind.Program, "ORDENTR").Findings, f => f.Code == "PGM004");  // uses the display file

        var ck = Diff(r, ObjectKind.CheckConstraint, "ORDLIN_PRICE_CK", "ORDER_LINE");
        Assert.Contains(ck.PreChecks, p => p.Sql.Contains("NOT (PRICE >= 0)"));
        Assert.True(Has(Diff(r, ObjectKind.Alias, "ORDHIST_PRD"), "ALS002"));
        var total = Diff(r, ObjectKind.Column, "TOTAL", "ORDER_HEADER");
        Assert.Contains(total.Changes, c => c.Attribute == "Text" && c.After == "Order total incl. tax");
        Assert.Contains(total.Changes, c => c.Attribute == "Data type" && c.Before == "DECIMAL(11,2)" && c.After == "DECIMAL(9,2)");
        Assert.True(Has(Diff(r, ObjectKind.ForeignKey, "ORDLIN_ORDHDR_FK"), "FK011"));
    }

    [Fact]
    public async Task Personal_library_to_master_reports_only_what_the_developer_changed()
    {
        var r = await TestEnv.Service(out _).CompareLibrariesAsync("DEV", "SMAMIDI", "ORDDEV", sourceOnly: true, programLibrary: "ORDPGMDEV", sourceFileLibrary: "ORDSRCDEV");
        Assert.Contains(r.Differences, d => d.Kind == ObjectKind.Column && d.Name == "NOTE" && d.Change == ChangeKind.Added);
        var sp = r.Differences.Single(d => d.Kind == ObjectKind.Procedure && d.Name == "SP_CREATE_ORDER");
        Assert.DoesNotContain("SMAMIDI", sp.SourceDdl);                          // personal library name rewritten to ORDDEV
        Assert.Contains(r.Differences, d => d.Kind == ObjectKind.Program && d.Name == "ORDENTR" && d.Change == ChangeKind.Modified);
        Assert.DoesNotContain(r.Differences, d => d.Change == ChangeKind.Removed && d.Kind != ObjectKind.Column);
        Assert.DoesNotContain(r.Differences, d => d.Kind == ObjectKind.Authority);
        Assert.Contains(r.GlobalFindings, f => f.Code == "LIB005");
        var entr = r.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == "ORDENTR");
        Assert.Equal("ORDPGMDEV", entr.TargetLibrary);
        var plan = new DeploymentPlanner().Build(r, [entr.Id], "T", "B");
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CPYF FROMFILE(SMAMIDI/QRPGLESRC) TOFILE(ORDSRCDEV/QRPGLESRC)"));
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CRTBNDRPG PGM(ORDPGMDEV/ORDENTR) SRCFILE(ORDSRCDEV/QRPGLESRC)"));

        var full = await TestEnv.Service(out _).CompareLibrariesAsync("DEV", "SMAMIDI", "ORDDEV", sourceOnly: false);
        Assert.Contains(full.Differences, d => d.Change == ChangeKind.Removed);   // without the flag the master's other objects show up
    }
}

public class SnapshotTests
{
    [Fact]
    public async Task Snapshots_dedupe_retain_and_reject_unsafe_ids()
    {
        var o = TestEnv.Options();
        var store = new FileSnapshotStore(TestEnv.TempDir());
        var snap = await TestEnv.Catalog(o).LoadAsync(o.Env("QA"));
        var a = store.Save(snap, r => r.CapturedBy = "t", retain: 2);
        var b = store.Save(snap, r => r.CapturedBy = "t", retain: 2);
        Assert.Equal(a.PayloadId, b.PayloadId);                                  // unchanged structure: no second copy
        Assert.True(b.Unchanged);
        Assert.Equal(snap.Fingerprint(), store.Load("QA", b.Id).Fingerprint());
        store.Save(snap, _ => { }, retain: 2);
        Assert.Equal(2, store.List("QA").Count);                                 // retention
        Assert.Null(store.Get("QA", "../../web.config"));
        Assert.Throws<TurnoverException>(() => store.Load("QA", "..\\x"));
        Assert.Throws<TurnoverException>(() => store.List("../QA"));
    }

    [Fact]
    public async Task Fingerprint_ignores_row_counts_but_not_structure()
    {
        var o = TestEnv.Options();
        var snap = await TestEnv.Catalog(o).LoadAsync(o.Env("QA"));
        var copy = System.Text.Json.JsonSerializer.Deserialize<EnvironmentSnapshot>(System.Text.Json.JsonSerializer.Serialize(snap))!;
        copy.Libraries["ORDQA"].Tables[0].RowCount += 12345;
        copy.Libraries["ORDQA"].Sequences[0].NextValue += 10;
        Assert.Equal(snap.Fingerprint(), copy.Fingerprint());
        copy.Libraries["ORDQA"].Tables[0].Columns[0].Length = 99;
        Assert.NotEqual(snap.Fingerprint(), copy.Fingerprint());
    }

    [Fact]
    public async Task History_comparison_shows_what_changed_inside_one_environment()
    {
        var o = TestEnv.Options();
        var before = await TestEnv.Catalog(o).LoadAsync(o.Env("QA"));
        var after = System.Text.Json.JsonSerializer.Deserialize<EnvironmentSnapshot>(System.Text.Json.JsonSerializer.Serialize(before))!;
        after.Libraries["ORDQA"].Table("ORDER_HEADER")!.Columns.Add(new ColumnDef { Name = "PROMO", SystemName = "PROMO", DataType = "CHAR", Length = 5, Ordinal = 9 });
        var r = TurnoverService.CompareHistory(o, o.Env("QA"), before, after);
        var d = Assert.Single(r.Differences);                                   // only the change: no standing drift, no recompile noise
        Assert.Equal("PROMO", d.Name);
        Assert.Contains(r.GlobalFindings, f => f.Code == "HIST001");
        Assert.DoesNotContain(r.GlobalFindings, f => f.Code.StartsWith("PATH"));
    }

    [Fact]
    public async Task Comparing_against_a_snapshot_uses_the_stored_side()
    {
        var svc = TestEnv.Service(out _, out var store);
        var o = TestEnv.Options();
        var rec = store.Save(await TestEnv.Catalog(o).LoadAsync(o.Env("QA")), _ => { }, 10);
        var r = await svc.CompareAsync("DEV", "QA", null, targetSnapshot: rec.Id);
        Assert.Equal(rec.Id, r.TargetLabel);
        Assert.False(r.IsLiveOnBothSides);
    }
}

public class SafetyAndPerformanceTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("ORDER_HEADER", "ORDER_HEADER")]
    [InlineData("a,b", "\"a,b\"")]
    public void Csv_cells_cannot_become_formulas(string input, string expected) => Assert.Equal(expected, ChangeReport.Cell(input));

    [Fact]
    public async Task Csv_export_has_bom_header_and_one_row_per_attribute()
    {
        var r = await TestEnv.Service(out _).CompareAsync("DEV", "QA", null);
        var bytes = ChangeReport.Csv(r);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("Type,Library,Object,Change,Severity,Attribute,Target now,After promotion,Rules", text);
        Assert.Contains("\"DECIMAL(11,2)\",\"DECIMAL(9,2)\"", text); // values with commas are quoted
        Assert.DoesNotContain("pwd=", text);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(42, 42L)]
    [InlineData("123", 123L)]
    [InlineData("12.5", null)]
    [InlineData("abc", null)]
    public void Numbers_from_the_driver_never_throw(object? value, long? expected) => Assert.Equal(expected, SqlText.ToLong(value));

    [Fact]
    public void Decimal31_beyond_long_is_null_not_an_exception()
    {
        Assert.Null(SqlText.ToLong(9_999_999_999_999_999_999_999_999m));
        Assert.Null(SqlText.ToLong(DBNull.Value));
        Assert.Equal(5L, SqlText.ToLong(5.9m));
    }

    [Fact]
    public void Identifiers_are_quoted_and_cl_names_validated()
    {
        Assert.Equal("ORDER_HEADER", SqlText.Id("ORDER_HEADER"));
        Assert.Equal("\"my\"\"tab\"", SqlText.Id("my\"tab"));
        Assert.Throws<TurnoverException>(() => SqlText.ClName("BAD NAME)"));
        Assert.Equal("CALL QSYS2.QCMDEXC('SNDMSG MSG(''hi'')')", Ddl.Qcmdexc("SNDMSG MSG('hi')"));
        Assert.Throws<TurnoverException>(() => Ddl.Rule("DROP TABLE X"));
    }

    [Fact]
    public void Identifier_tokens_include_qualified_names()
    {
        var ids = SqlText.Identifiers("select x from ORDQA.ORDER_HEADER h, \"Mixed\".t where 'ORDQA'");
        Assert.Contains("ORDQA.ORDER_HEADER", ids);
        Assert.Contains("ORDER_HEADER", ids);
        Assert.Contains("Mixed.T", ids);
    }

    [Fact]
    public void Text_diff_of_huge_bodies_stays_fast_and_bounded()
    {
        var a = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => $"line {i}"));
        var b = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => i % 3 == 0 ? $"changed {i}" : $"line {i}"));
        var sw = Stopwatch.StartNew();
        var d = TextDiff.Compute(a, b);
        Assert.True(sw.ElapsedMilliseconds < 15000, $"took {sw.ElapsedMilliseconds} ms");   // generous: shared build agents are slow; this guards against quadratic blow-ups, not jitter
        Assert.True(d.Count >= 20_000);
    }

    [Fact]
    public void Library_remap_is_linear_on_long_bodies()
    {
        var o = TestEnv.Options();
        var map = LibraryMap.ForPromotion(o.Env("DEV"), o.Env("QA"), o.Environments);
        var body = string.Join("\n", Enumerable.Range(0, 30_000).Select(i => $"SELECT * FROM ORDDEV.T{i};"));
        var sw = Stopwatch.StartNew();
        var r = map.Remap(body);
        Assert.True(sw.ElapsedMilliseconds < 15000, $"took {sw.ElapsedMilliseconds} ms");   // generous: shared build agents are slow; this guards against quadratic blow-ups, not jitter
        Assert.Equal(30_000, r.Rewrites.Count);
        Assert.Equal(30_000, r.Rewrites[^1].Line);
    }

    [Fact]
    public void Map_text_parsing_fails_closed()
    {
        Assert.Equal("ORDDEV", MapText.Parse("ORDERS=orddev; CUSTOMER=CUSDEV", "x")["ORDERS"]);
        Assert.Throws<TurnoverException>(() => MapText.Parse("ORDERS", "x"));
        Assert.Throws<TurnoverException>(() => MapText.Parse("A=B;A=C", "x"));
    }
}
