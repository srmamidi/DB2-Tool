using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

public class PlannerTests
{
    private static async Task<ComparisonResult> DevQa() => await TestEnv.Service(out _).CompareAsync("DEV", "QA", null);
    private static string Id(ComparisonResult r, ObjectKind k, string name, string? parent = null) =>
        r.Differences.Single(d => d.Kind == k && d.Name == name && (parent is null || d.Parent == parent)).Id;

    [Fact]
    public async Task Fk_selected_without_its_parent_table_is_blocked()
    {
        var r = await DevQa();
        var plan = new DeploymentPlanner().Build(r, [Id(r, ObjectKind.ForeignKey, "ORDHDR_SHIP_FK"), Id(r, ObjectKind.Column, "SHIP_ID", "ORDER_HEADER")], "TOV-00001", "TB00000001");
        Assert.Contains(plan.Findings, f => f.Code == "PLAN001" && f.Message.Contains("SHIPPER"));
    }

    [Fact]
    public async Task Table_change_without_recompile_is_blocked_and_recompile_without_cause_is_dropped()
    {
        var r = await DevQa();
        // CUSTOMER.STATUS becomes NOT NULL → new record format → CUSMNT must be recompiled.
        var plan = new DeploymentPlanner().Build(r, [Id(r, ObjectKind.Column, "STATUS", "CUSTOMER")], "TOV-1", "TB1");
        Assert.Contains(plan.Findings, f => f.Code == "PLAN003" && f.Message.Contains("CUSMNT"));

        var lonelyRecompile = new DeploymentPlanner().Build(r, [Id(r, ObjectKind.Program, "CUSMNT")], "TOV-1", "TB1");
        Assert.DoesNotContain(lonelyRecompile.Steps, s => s.Title.Contains("CUSMNT"));
    }

    [Fact]
    public async Task Steps_run_in_safe_order_with_prechecks_backups_and_rollbacks()
    {
        var r = await DevQa();
        var ids = new[]
        {
            Id(r, ObjectKind.Table, "SHIPPER"), Id(r, ObjectKind.Column, "SHIP_ID", "ORDER_HEADER"), Id(r, ObjectKind.ForeignKey, "ORDHDR_SHIP_FK"),
            Id(r, ObjectKind.Program, "ORDTOTR"), Id(r, ObjectKind.Program, "ORDENTR"), Id(r, ObjectKind.Sequence, "ORDER_SEQ"),
            Id(r, ObjectKind.ForeignKey, "ORDLIN_ORDHDR_FK"),
        };
        var plan = new DeploymentPlanner().Build(r, ids, "TOV-00007", "TB00000007");
        // The only blocker is the sample's deliberate CHECK PENDING constraint in QA (FK011).
        Assert.All(plan.Findings.Where(f => f.Severity == Severity.Blocker), f => Assert.Equal("FK011", f.Code));

        int Pos(Func<PlanStep, bool> f) => plan.Steps.First(f).Order;
        Assert.Equal(1, Pos(s => s.Command.StartsWith("CRTLIB LIB(TB00000007)")));
        Assert.True(Pos(s => s.Phase == StepPhase.PreCheck && s.Title.Contains("orphan")) < Pos(s => s.Phase == StepPhase.Backup));
        Assert.True(Pos(s => s.Title.StartsWith("Drop foreign key ORDLIN_ORDHDR_FK")) < Pos(s => s.Title.StartsWith("Re-add foreign key ORDLIN_ORDHDR_FK")));
        Assert.True(Pos(s => s.Phase == StepPhase.Backup) < Pos(s => s.Title == "Create table SHIPPER"));
        Assert.True(Pos(s => s.Title == "Create table SHIPPER") < Pos(s => s.Title == "Add foreign key ORDHDR_SHIP_FK"));
        Assert.True(Pos(s => s.Title.StartsWith("Add column ORDER_HEADER.SHIP_ID")) < Pos(s => s.Title.Contains("Recompile ORDTOTR")));
        Assert.Contains(plan.Steps, s => s.Command == "ALTER SEQUENCE ORDQA.ORDER_SEQ RESTART WITH 90551");
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CRTDUPOBJ OBJ(ORDHDR) FROMLIB(ORDQA) OBJTYPE(*FILE) TOLIB(TB00000007)") && s.Command.Contains("DATA(*YES)"));
        // Programs restore from backup on rollback; every rollback is executable SQL or an explicit manual note.
        Assert.Contains(plan.Steps, s => s.Rollback?.Contains("CRTDUPOBJ OBJ(ORDENTR) FROMLIB(TB00000007)") == true);
        Assert.All(plan.Steps.Where(s => s.Rollback is not null), s => Assert.True(
            s.Rollback!.StartsWith("CALL QSYS2.QCMDEXC") || DeploymentPlanner.IsManual(s.Rollback)
            || new[] { "DROP ", "ALTER ", "CREATE ", "LABEL ", "COMMENT ", "RENAME ", "UPDATE ", "DELETE ", "INSERT ", "GRANT ", "REVOKE " }.Any(v => s.Rollback.StartsWith(v, StringComparison.OrdinalIgnoreCase)),
            $"{s.Title}: {s.Rollback}"));
    }

    [Theory]
    [InlineData("DEV", "QA")]
    [InlineData("QA", "STAGE")]
    [InlineData("STAGE", "PROD")]
    public async Task Recommended_selection_never_leaves_a_dependency_or_recompile_behind(string src, string tgt)
    {
        var r = await TestEnv.Service(out _).CompareAsync(src, tgt, null);
        var plan = new DeploymentPlanner().Build(r, r.RecommendedSelection(), "T", "B");
        Assert.DoesNotContain(plan.Findings, f => f.Code is "PLAN001" or "PLAN003");
        Assert.DoesNotContain(plan.Findings, f => f.Severity == Severity.Blocker && f.Code.StartsWith("PATH") == false && !f.AutoFixed);
    }

    [Fact]
    public async Task Script_hash_is_stable_for_the_same_selection()
    {
        var r1 = await DevQa();
        var r2 = await DevQa();
        var ids = r1.Differences.Where(d => d.DefaultSelected && !d.HasOpenBlocker).Select(d => d.Id).ToList();
        Assert.Equal(new DeploymentPlanner().Build(r1, ids, "T", "B").ScriptHash, new DeploymentPlanner().Build(r2, ids, "T", "B").ScriptHash);
    }

    [Fact]
    public async Task Rename_preserves_data_and_routines_follow_the_columns_they_use()
    {
        var r = await DevQa();
        var sp = r.Differences.Single(d => d.Name == "SP_CREATE_ORDER");
        Assert.Contains(Id(r, ObjectKind.Column, "ORDER_STATUS", "ORDER_HEADER"), sp.DependsOn);

        var plan = new DeploymentPlanner().Build(r, [Id(r, ObjectKind.Column, "ORDER_STATUS", "ORDER_HEADER")], "T", "B");
        var cmds = plan.Steps.Select(s => s.Command).ToList();
        int i(string s) => cmds.FindIndex(c => c.StartsWith(s));
        Assert.True(i("ALTER TABLE ORDQA.ORDER_HEADER ADD COLUMN ORDER_STATUS") < i("UPDATE ORDQA.ORDER_HEADER SET ORDER_STATUS = STATUS"));
        Assert.True(i("UPDATE ORDQA.ORDER_HEADER") < i("ALTER TABLE ORDQA.ORDER_HEADER DROP COLUMN STATUS RESTRICT"));
    }
}

public class WorkflowTests
{
    private static async Task<(TurnoverService Svc, TurnoverPackage Pkg)> NewPackage(string src = "DEV", string tgt = "QA") =>
        await NewPackage(src, tgt, out _);

    private static Task<(TurnoverService Svc, TurnoverPackage Pkg)> NewPackage(string src, string tgt, out JsonFileTurnoverRepository repo)
    {
        var svc = TestEnv.Service(out repo);
        return Build(svc, src, tgt);
    }

    private static async Task<(TurnoverService Svc, TurnoverPackage Pkg)> Build(TurnoverService svc, string src, string tgt)
    {
        var cmp = await svc.CompareAsync(src, tgt, null);
        var ids = cmp.RecommendedSelection().ToList();
        var pkg = await svc.CreatePackageAsync(TestEnv.Dev, src, tgt, [], ids, "Order audit release", "CHG-1234");
        return (svc, pkg);
    }

    [Fact]
    public async Task Requester_cannot_approve_and_roles_are_enforced()
    {
        var (svc, pkg) = await NewPackage();
        await svc.SubmitAsync(TestEnv.Dev, pkg.Number, "Demo override: blockers are tracked under CHG-1234 follow-up.");
        var self = new TurnoverUser("alice", [Roles.Developer, Roles.Approver]);
        await Assert.ThrowsAsync<TurnoverException>(() => svc.DecideAsync(self, pkg.Number, true, "lgtm"));
        await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, pkg.Number)); // not approved yet
        var approved = await svc.DecideAsync(TestEnv.Approver1, pkg.Number, true, "ok");
        Assert.Equal(PackageStatus.Approved, approved.Status);
        Assert.NotEmpty(approved.ApprovedSteps);
    }

    [Fact]
    public async Task Prod_needs_two_approvals_and_no_blocker_override()
    {
        // Both paths, every run: a blocked Prod package is refused even with a justification ...
        var svc = TestEnv.Service(out _);
        var cmp = await svc.CompareAsync("STAGE", "PROD", null);
        // QA → PROD skips Stage (PATH001), a blocker no justification can override in production.
        var skip = await svc.CompareAsync("QA", "PROD", null);
        var bad = await svc.CreatePackageAsync(TestEnv.Dev, "QA", "PROD", [], [skip.Differences.First(d => d.Kind == ObjectKind.Column).Id], "blocked", "CHG-B");
        Assert.True((await svc.AnalyzeAsync(bad)).Plan.HasOpenBlockers);
        await Assert.ThrowsAsync<TurnoverException>(() => svc.SubmitAsync(TestEnv.Dev, bad.Number, "Please let this one through, it is urgent."));
        // ... and a clean one needs two approvals.
        // (the recommended selection is dependency-closed, so it carries no blockers)
        var pkg = await svc.CreatePackageAsync(TestEnv.Dev, "STAGE", "PROD", [], cmp.RecommendedSelection(), "clean", "CHG-C");
        Assert.False((await svc.AnalyzeAsync(pkg)).Plan.HasOpenBlockers);
        await svc.SubmitAsync(TestEnv.Dev, pkg.Number, null);
        var one = await svc.DecideAsync(TestEnv.Approver1, pkg.Number, true, "ok");
        Assert.Equal(PackageStatus.Submitted, one.Status);
        var two = await svc.DecideAsync(TestEnv.Approver2, pkg.Number, true, "ok");
        Assert.Equal(PackageStatus.Approved, two.Status);
    }

    [Fact]
    public async Task Deploy_refuses_a_script_that_differs_from_the_approved_one_and_rollback_uses_frozen_steps()
    {
        var (svc, pkg) = await NewPackage("DEV", "QA", out var store);
        await svc.SubmitAsync(TestEnv.Dev, pkg.Number, "Demo override: blockers are tracked under CHG-1234 follow-up.");
        await svc.DecideAsync(TestEnv.Approver1, pkg.Number, true, "ok");

        // Tamper with what was approved: deploy must refuse.
        var p = store.Get(pkg.Number)!;
        var good = p.ApprovedScriptHash;
        p.ApprovedScriptHash = "TAMPERED";
        store.Save(p);
        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, pkg.Number));
        Assert.Contains("not the script that was approved", ex.Message);
        p.ApprovedScriptHash = good;
        store.Save(p);

        var run = await svc.DeployAsync(TestEnv.Release, pkg.Number);
        Assert.True(run.Succeeded);
        var back = await svc.RollbackAsync(TestEnv.Release, pkg.Number);
        Assert.NotEmpty(back.Results);
        Assert.Equal(PackageStatus.RolledBack, svc.Package(pkg.Number).Status);
    }
}

public class LibraryMapAndOptionsTests
{
    private static LibraryMap Map()
    {
        var o = TestEnv.Options();
        return LibraryMap.ForPromotion(o.Env("DEV"), o.Env("QA"), o.Environments);
    }

    [Fact]
    public void Remap_is_whole_word_and_reports_literals_and_foreign_libraries()
    {
        var r = Map().Remap("SELECT * FROM ORDDEV.T, ORDDEV2.X, ORDPRD.H WHERE A = 'ORDDEV/Q'");
        Assert.Equal("SELECT * FROM ORDQA.T, ORDDEV2.X, ORDPRD.H WHERE A = 'ORDQA/Q'", r.Text);
        Assert.Contains(r.Rewrites, x => x.InStringLiteral);
        Assert.Contains(r.Foreign, f => f.Library == "ORDPRD" && f.OwnerEnvironment == "PROD");
    }

    [Fact]
    public void External_name_and_qualified_objects_are_mapped()
    {
        var m = Map();
        Assert.Equal("ORDPGMQA/ORDTOTR", m.MapQualifiedObject("ORDPGMDEV/ORDTOTR"));
        Assert.Equal("*LIBL/ORDTOTR", m.MapQualifiedObject("*LIBL/ORDTOTR"));
        Assert.True(m.Remap("EXTERNAL NAME 'ORDPGMDEV/ORDTOTR'").Rewrites.Single().InExternalName);
    }

    [Fact]
    public void Options_fail_closed()
    {
        var shared = TestEnv.Options();
        shared.Env("QA").Libraries["ORDERS"] = "ORDDEV"; // same host as DEV
        Assert.Throws<TurnoverException>(() => shared.Validate());

        var live = TestEnv.Options();
        live.Mode = "Live";
        foreach (var e in live.Environments) e.ConnectionStringName = "Db2" + e.Key;
        Assert.Throws<TurnoverException>(() => live.Validate(_ => null)); // missing connection string

        var badName = TestEnv.Options();
        badName.Env("DEV").Libraries["ORDERS"] = "1BAD";
        Assert.Throws<TurnoverException>(() => badName.Validate());

        var badMode = TestEnv.Options();
        badMode.Mode = "Liv";
        Assert.Throws<TurnoverException>(() => badMode.Validate());
    }

    [Theory]
    [InlineData("DECIMAL", 11, 2, "DECIMAL", 9, 2, TypeChange.Narrowing)]
    [InlineData("VARCHAR", 120, null, "VARCHAR", 254, null, TypeChange.Widening)]
    [InlineData("INTEGER", null, null, "BIGINT", null, null, TypeChange.Widening)]
    [InlineData("BIGINT", null, null, "INTEGER", null, null, TypeChange.Narrowing)]
    [InlineData("CHAR", 10, null, "VARCHAR", 10, null, TypeChange.SemanticChange)]
    [InlineData("CHAR", 10, null, "INTEGER", null, null, TypeChange.Incompatible)]
    public void Type_changes_are_classified(string ft, int? fl, int? fs, string tt, int? tl, int? ts, TypeChange expected) =>
        Assert.Equal(expected, TypeCompat.Classify(new ColumnDef { DataType = ft, Length = fl, Scale = fs }, new ColumnDef { DataType = tt, Length = tl, Scale = ts }));

    [Fact]
    public void Text_diff_marks_changed_lines()
    {
        var d = TextDiff.Compute("a\nb\nc", "a\nB\nc");
        Assert.Equal([LineOp.Same, LineOp.Removed, LineOp.Added, LineOp.Same], d.Select(x => x.Op));
    }
}

public class LibraryMoveTests
{
    [Fact]
    public async Task Moving_a_library_repoints_foreign_keys_and_views_in_other_libraries()
    {
        var o = TestEnv.Options();
        var move = await new LibraryMoveAnalyzer(TestEnv.Catalog(o), o).AnalyzeAsync("QA", "CUSQA", "CUSQA2", keepOriginal: true);
        Assert.Contains(move.Steps, s => s.Command.StartsWith("CPYLIB FROMLIB(CUSQA) TOLIB(CUSQA2)"));
        Assert.Contains(move.Steps, s => s.Command.Contains("REFERENCES CUSQA2.CUSTOMER"));
        Assert.Contains(move.Steps, s => s.Title.Contains("V_OPEN_ORDERS") && s.Command.Contains("CUSQA2.CUSTOMER"));
        Assert.Contains(move.Impacts, i => i.Object == "CUSMNT" && i.Issue.Contains("library list"));
        Assert.DoesNotContain(move.Steps, s => s.Command.StartsWith("DLTLIB LIB(CUSQA)"));
    }
}
