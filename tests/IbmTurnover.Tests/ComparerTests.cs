using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Tests;

public class ComparerTests
{
    private static async Task<ComparisonResult> Compare(string src, string tgt) =>
        await TestEnv.Service(out _).CompareAsync(src, tgt, null);

    private static Difference Diff(ComparisonResult r, ObjectKind kind, string name, string? parent = null) =>
        r.Differences.SingleOrDefault(d => d.Kind == kind && d.Name == name && (parent is null || d.Parent == parent))
        ?? throw new Xunit.Sdk.XunitException($"No {kind} diff for {parent}.{name}. Have: {string.Join(", ", r.Differences.Select(d => d.Kind + ":" + d.DisplayName))}");

    private static bool Has(Difference d, string code) => d.Findings.Any(f => f.Code == code);

    [Fact]
    public async Task Dev_to_QA_catches_column_level_risks()
    {
        var r = await Compare("DEV", "QA");

        var loyalty = Diff(r, ObjectKind.Column, "LOYALTY_TIER", "CUSTOMER");
        Assert.True(Has(loyalty, "COL002")); // NOT NULL, no default, table has rows
        Assert.True(loyalty.HasOpenBlocker);

        var total = Diff(r, ObjectKind.Column, "TOTAL", "ORDER_HEADER");
        Assert.Contains(total.Findings, f => f.Code == "COL003" && f.Severity == Severity.Blocker);
        Assert.Contains(total.PreChecks, p => p.Sql.Contains("ABS(TOTAL) >= 10000000") && p.Sql.Contains("ORDQA.ORDER_HEADER"));

        var rename = Diff(r, ObjectKind.Column, "ORDER_STATUS", "ORDER_HEADER");
        Assert.Equal(ChangeKind.Renamed, rename.Change);
        Assert.Equal("STATUS", ((ColumnDef)rename.Target!).Name);
        // QA's SP_CREATE_ORDER and V_OPEN_ORDERS still use STATUS: the rename must ship with their replacements.
        Assert.Contains(Diff(r, ObjectKind.Procedure, "SP_CREATE_ORDER").Id, rename.DependsOn);
        Assert.Contains(Diff(r, ObjectKind.View, "V_OPEN_ORDERS").Id, rename.DependsOn);
    }

    [Fact]
    public async Task Dev_to_QA_catches_system_name_identity_and_sequence_drift()
    {
        var r = await Compare("DEV", "QA");
        Assert.True(Has(Diff(r, ObjectKind.Table, "ORDER_LINE"), "TBL003"));

        var ident = Diff(r, ObjectKind.Column, "CUST_ID", "CUSTOMER");
        Assert.True(Has(ident, "COL009"));
        Assert.Equal(5121, ident.RestartWith);

        var seq = Diff(r, ObjectKind.Sequence, "ORDER_SEQ");
        Assert.True(Has(seq, "SEQ003"));
        Assert.Equal(90551, seq.RestartWith);
        Assert.False(seq.HasOpenBlocker); // auto-fixed by the script
        Assert.Contains("CACHE 50 → 20", seq.Details);

        var newSeq = Diff(r, ObjectKind.Sequence, "AUDIT_SEQ");
        Assert.Equal(ChangeKind.Added, newSeq.Change);
        Assert.DoesNotContain("START WITH 121", newSeq.SourceDdl); // never copies the Dev current value
    }

    [Fact]
    public async Task Dev_to_QA_catches_foreign_key_and_index_mismatches()
    {
        var r = await Compare("DEV", "QA");

        var usr = Diff(r, ObjectKind.ForeignKey, "ORDAUD_USR_FK");
        Assert.True(Has(usr, "FK004")); // SECDEV is not mapped
        Assert.True(usr.HasOpenBlocker);

        var cascade = Diff(r, ObjectKind.ForeignKey, "ORDLIN_ORDHDR_FK");
        Assert.Equal(ChangeKind.Modified, cascade.Change);
        Assert.Contains(cascade.Findings, f => f.Code == "FK005" && f.Severity == Severity.Warning);

        var ship = Diff(r, ObjectKind.ForeignKey, "ORDHDR_SHIP_FK");
        Assert.Contains(Diff(r, ObjectKind.Table, "SHIPPER").Id, ship.DependsOn);
        Assert.Contains(Diff(r, ObjectKind.Column, "SHIP_ID", "ORDER_HEADER").Id, ship.DependsOn);
        // SHIP_ID is a new nullable column: existing rows are NULL, never orphans, and the column does not exist
        // yet when pre-checks run, so there must be no pre-check and no orphan warning.
        Assert.Empty(ship.PreChecks);
        Assert.False(Has(ship, "FK008"));

        Assert.True(Has(Diff(r, ObjectKind.Index, "ORDLIN_ITEM_IX"), "IDX005")); // same keys, different name
        Assert.False(Diff(r, ObjectKind.Index, "ORDLIN_ITEM_IX").DefaultSelected);
        Assert.True(Has(Diff(r, ObjectKind.Index, "ORDHDR_CUST_IX"), "IDX002"));
        // UNIQUE on a column this package adds to 90,550 rows: all NULL, and Db2 for i UNIQUE treats NULLs as equal.
        var ux = Diff(r, ObjectKind.Index, "ORDHDR_EXTREF_UX");
        Assert.True(Has(ux, "IDX008"));
        Assert.True(ux.HasOpenBlocker);
        Assert.Empty(ux.PreChecks);
    }

    [Fact]
    public async Task Dev_to_QA_catches_routine_library_and_overload_problems()
    {
        var r = await Compare("DEV", "QA");

        var sp = Diff(r, ObjectKind.Procedure, "SP_CREATE_ORDER");
        Assert.True(Has(sp, "RTN003")); // ORDPRD belongs to PROD
        Assert.True(Has(sp, "RTN002")); // CUSDEV / ORDDEV rewritten
        Assert.True(Has(sp, "RTN007")); // ORDDEV inside the QCMDEXC literal
        Assert.Contains("CUSQA.CUSTOMER", sp.SourceDdl);
        Assert.DoesNotContain("ORDDEV", sp.SourceDdl);

        var total = Diff(r, ObjectKind.Procedure, "SP_ORDER_TOTAL");
        Assert.True(Has(total, "RTN005")); // parameter count changed → overload
        Assert.Contains("ORDPGMQA/ORDTOTR", total.SourceDdl);
        Assert.False(Has(total, "RTN002")); // own name + EXTERNAL NAME are not noise

        Assert.DoesNotContain(r.Differences, d => d.Name == "FN_ORDER_AGE"); // identical after library remap
    }

    [Fact]
    public async Task Dev_to_QA_catches_rpg_hotfix_stale_object_and_level_checks()
    {
        var r = await Compare("DEV", "QA");

        Assert.True(Has(Diff(r, ObjectKind.ServiceProgram, "ORDUTIL"), "PGM005")); // QA emergency fix would be overwritten
        Assert.True(Has(Diff(r, ObjectKind.Program, "ORDRPT"), "PGM002"));         // compiled before source changed
        Assert.True(Has(Diff(r, ObjectKind.Program, "SHPLBLR"), "PGM010"));        // hard-coded ORDDEV/ORDHDR

        var tot = Diff(r, ObjectKind.Program, "ORDTOTR");                           // unchanged source, but opens ORDHDR
        Assert.Equal(ChangeKind.Drift, tot.Change);
        Assert.True(Has(tot, "PGM004"));
        Assert.True(Has(Diff(r, ObjectKind.Program, "CUSMNT"), "PGM004"));

        Assert.True(Has(Diff(r, ObjectKind.View, "V_ORDER_LINES"), "VIEW002"));    // SELECT * over a changed table
        Assert.True(Has(Diff(r, ObjectKind.Table, "TMP_FIX_0412"), "TBL002"));
        Assert.False(Diff(r, ObjectKind.Table, "TMP_FIX_0412").DefaultSelected);
    }

    [Theory]
    [InlineData("DEV", "QA")]
    [InlineData("QA", "STAGE")]
    [InlineData("STAGE", "PROD")]
    public async Task Pre_checks_only_reference_tables_and_columns_that_exist_before_deployment(string src, string tgt)
    {
        var o = TestEnv.Options();
        var svc = TestEnv.Service(out _, o);
        var r = await svc.CompareAsync(src, tgt, null);
        var target = (await TestEnv.Catalog(o).LoadAsync(o.Env(tgt))).Libraries;
        foreach (var pc in r.Differences.SelectMany(d => d.PreChecks.Select(x => (d, x))))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pc.x.Sql, @"FROM ([A-Z0-9_#@$]+)\.([A-Z0-9_#@$]+)"))
            {
                if (m.Groups[1].Value is "QSYS2" or "SYSIBM") continue;
                var table = target.GetValueOrDefault(m.Groups[1].Value)?.Table(m.Groups[2].Value);
                Assert.True(table is not null, $"{pc.d.DisplayName}: pre-check reads {m.Value}, which does not exist in {tgt} before deployment.");
                if (pc.d.Kind == ObjectKind.ForeignKey && pc.d.Source is ForeignKeyDef fk && table.Name == fk.Table)
                    Assert.All(fk.Columns, col => Assert.True(table.Column(col) is not null, $"{fk.Name}: column {col} does not exist yet."));
            }
    }

    [Fact]
    public async Task Promotion_path_is_enforced()
    {
        var skip = await Compare("DEV", "PROD");
        Assert.Contains(skip.GlobalFindings, f => f.Code == "PATH001" && f.Severity == Severity.Blocker);
        var down = await Compare("PROD", "STAGE");
        Assert.Contains(down.GlobalFindings, f => f.Code == "PATH002");
    }

    [Fact]
    public async Task Upper_tiers_flag_journaling_and_prod_only_index()
    {
        var qaStage = await Compare("QA", "STAGE");
        Assert.True(Has(Diff(qaStage, ObjectKind.Table, "ORDER_LINE"), "TBL004"));

        var stageProd = await Compare("STAGE", "PROD");
        var prdIx = Diff(stageProd, ObjectKind.Index, "PRD_ORDHDR_DATE_IX");
        Assert.Equal(ChangeKind.Removed, prdIx.Change);
        Assert.False(prdIx.DefaultSelected);
    }

    [Fact]
    public async Task Same_instance_library_copy_creates_the_library()
    {
        var r = await TestEnv.Service(out _).CompareLibrariesAsync("QA", "ORDQA", "ORDQA2");
        var lib = Diff(r, ObjectKind.Library, "ORDQA2");
        Assert.Equal(ChangeKind.Added, lib.Change);
        Assert.Contains(r.Differences, d => d.Kind == ObjectKind.Table && d.Name == "ORDER_HEADER" && d.Change == ChangeKind.Added);
        // CUSQA is another QA library: mapped to itself, not reported as unknown.
        Assert.DoesNotContain(r.Differences.SelectMany(d => d.Findings), f => f.Code == "FK004");
    }
}
