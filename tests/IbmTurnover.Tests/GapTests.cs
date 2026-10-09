using IbmTurnover.Core;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

/// <summary>The three high-risk gaps: source check-out, source version history, compile fidelity.</summary>
public class CheckoutTests
{
    private static readonly TurnoverUser Carol = new("carol", [Roles.Developer]);

    [Fact]
    public void A_member_has_one_owner_across_environments()
    {
        TestEnv.Service(out _, out _, out var co, out _);
        co.CheckOut(TestEnv.Dev, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDENTR", "CHG-1");
        // Same logical member, seen from QA: still alice's.
        var ex = Assert.Throws<TurnoverException>(() => co.CheckOut(Carol, "QA", "ORDSRCQA", "QRPGLESRC", "ORDENTR", "mine"));
        Assert.Contains("alice", ex.Message);
        Assert.Throws<TurnoverException>(() => co.CheckOut(TestEnv.Approver1, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDRPT", "x")); // not a developer
        Assert.Throws<TurnoverException>(() => co.CheckOut(TestEnv.Dev, "DEV", "ORDSRCDEV", "QRPGLESRC", "BAD NAME", "x"));
    }

    [Fact]
    public void Only_the_owner_or_a_release_manager_with_a_note_can_release()
    {
        TestEnv.Service(out _, out _, out var co, out _);
        var c = co.CheckOut(TestEnv.Dev, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDENTR", "CHG-1");
        Assert.Throws<TurnoverException>(() => co.Release(Carol, c.Id, "mine now"));
        Assert.Throws<TurnoverException>(() => co.Release(TestEnv.Release, c.Id, ""));
        Assert.False(co.Release(TestEnv.Release, c.Id, "alice left").Active);
    }

    [Fact]
    public async Task Someone_elses_check_out_blocks_submit_even_with_a_justification()
    {
        var svc = TestEnv.Service(out _, out _, out var co, out _);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var entr = cmp.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == "ORDENTR").Id;
        co.CheckOut(Carol, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDENTR", "carol's change");
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [entr], "Entry", "CHG-2");
        var (_, plan) = await svc.AnalyzeAsync(p);
        Assert.Contains(plan.Findings, f => f.Code == "PLAN010" && f.Message.Contains("carol"));
        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.SubmitAsync(TestEnv.Dev, p.Number, "Overriding because the release is urgent today."));
        Assert.Contains("carol", ex.Message);
    }

    [Fact]
    public async Task Required_check_out_is_satisfied_by_check_out_all_and_released_on_deploy()
    {
        var o = TestEnv.Options();
        o.Checkout.ReleaseOnTier = EnvironmentTier.QA;
        var svc = TestEnv.Service(out _, out _, out var co, out _, o);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var ids = new[] { "ORDENTR", "ORDRPT" }.Select(n => cmp.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == n).Id).ToList();
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], ids, "Programs", "CHG-3");
        Assert.Contains((await svc.AnalyzeAsync(p)).Plan.Findings, f => f.Code == "PLAN011");

        var (n, conflicts) = co.CheckOutAll(TestEnv.Dev, cmp, "CHG-3");
        Assert.True(n >= 2);
        Assert.Empty(conflicts);
        Assert.DoesNotContain((await svc.AnalyzeAsync(p)).Plan.Findings, f => f.Code is "PLAN010" or "PLAN011");

        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Level-check items are tracked separately in CHG-3.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        await svc.DeployAsync(TestEnv.Release, p.Number);
        Assert.Null(co.Holder("ORDSRC/QRPGLESRC(ORDENTR)"));                         // checked in on reaching QA
        Assert.Contains(svc.Package(p.Number).Audit, a => a.Action == "Check-outs released");
    }

    [Fact]
    public async Task Two_open_packages_on_the_same_object_are_flagged()
    {
        var svc = TestEnv.Service(out _, out _, out _, out _);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var col = cmp.Differences.Single(d => d.Kind == ObjectKind.Column && d.Name == "STATUS" && d.Parent == "CUSTOMER").Id;
        await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [col], "First", "CHG-4");
        var second = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [col], "Second", "CHG-5");
        Assert.Contains((await svc.AnalyzeAsync(second)).Plan.Findings, f => f.Code == "PLAN012" && f.Message.Contains("TOV-00001"));
    }
}

public class SourceHistoryTests
{
    [Fact]
    public async Task Approval_archives_before_and_after_and_the_package_shows_them()
    {
        var svc = TestEnv.Service(out _, out _, out var co, out var archive);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var entr = cmp.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == "ORDENTR").Id;
        co.CheckOut(TestEnv.Dev, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDENTR", "CHG-6");
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [entr], "Entry", "CHG-6");

        var live = await svc.Sources.LiveChangesAsync(p, cmp, default);
        var one = Assert.Single(live);
        Assert.DoesNotContain("ship method", one.Before);
        Assert.Contains("ship method", one.After);

        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Recompiles of other programs ship in CHG-7.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        var versions = archive.Versions("ORDSRC/QRPGLESRC(ORDENTR)");
        Assert.Contains(versions, v => v.Reason == "Before" && v.Environment == "QA" && v.Package == p.Code);
        Assert.Contains(versions, v => v.Reason == "After" && v.Environment == "DEV");
        var shown = svc.Sources.ArchivedChanges(svc.Package(p.Number), cmp).Single();
        Assert.Equal(one.After, shown.After);
    }

    [Fact]
    public void Identical_text_is_stored_once_and_unknown_hashes_are_refused()
    {
        var archive = new FileSourceArchive(TestEnv.TempDir());
        var a = archive.Save(new SourceVersion { Key = "K", Environment = "QA", Reason = "Manual" }, ["a", "b  "]);
        var b = archive.Save(new SourceVersion { Key = "K", Environment = "QA", Reason = "Manual" }, ["a", "b"]);
        Assert.Equal(a.Id, b.Id);                                                  // trailing blanks are not a change
        Assert.Equal("a\nb", archive.Text(a));
        Assert.Throws<TurnoverException>(() => archive.Text(new SourceVersion { Hash = "..\\..\\web.config" }));
    }
}

public class CompileFidelityTests
{
    private static async Task<(ComparisonResult R, DeploymentPlan Plan)> Plan(params string[] names)
    {
        var r = await TestEnv.Service(out _).CompareAsync("DEV", "QA", null);
        var ids = names.Select(n => r.Differences.Single(d => d.Name == n && d.Kind is ObjectKind.Program or ObjectKind.ServiceProgram).Id).ToList();
        return (r, new DeploymentPlanner().Build(r, ids, "T", "TB1"));
    }

    [Fact]
    public async Task Named_activation_group_is_rebuilt_in_two_steps_with_the_same_bindings_and_authority()
    {
        var (_, plan) = await Plan("ORDENTR");
        var cmds = plan.Steps.Select(s => s.Command).ToList();
        Assert.Contains(cmds, c => c.StartsWith("CRTRPGMOD MODULE(QTEMP/ORDENTR) SRCFILE(ORDSRCQA/QRPGLESRC) SRCMBR(ORDENTR) OPTIMIZE(*BASIC) DBGVIEW(*SOURCE) TGTRLS(V7R4M0)"));
        Assert.Contains(cmds, c => c == "CRTPGM PGM(ORDPGMQA/ORDENTR) MODULE(QTEMP/ORDENTR) BNDSRVPGM((ORDPGMQA/ORDUTIL)) ACTGRP(ORDERS) TGTRLS(V7R4M0) USRPRF(*OWNER) REPLACE(*YES)");
    }

    [Fact]
    public async Task Default_activation_group_and_sql_options_are_replayed()
    {
        var (r, plan) = await Plan("ORDRPT");
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CRTBNDRPG PGM(ORDPGMQA/ORDRPT)") && s.Command.Contains("DFTACTGRP(*YES)"));
        // ORDTOTR is recompiled for the level check from QA's own source with QA's own SQL options.
        var cust = r.Differences.Single(d => d.Kind == ObjectKind.Column && d.Name == "SHIP_ID").Id;
        var tot = r.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == "ORDTOTR").Id;
        var lc = new DeploymentPlanner().Build(r, [cust, tot], "T", "TB1");
        Assert.Contains(lc.Steps, s => s.Command.StartsWith("CRTSQLRPGI OBJ(QTEMP/ORDTOTR)") && s.Command.Contains("COMMIT(*NONE) NAMING(*SQL) DFTRDBCOL(ORDQA)"));
        Assert.Contains(lc.Steps, s => s.Command.StartsWith("CRTPGM PGM(ORDPGMQA/ORDTOTR)") && s.Command.Contains("ACTGRP(*CALLER)"));
    }

    [Fact]
    public async Task Service_program_uses_its_binder_source_which_travels_with_it()
    {
        var r = await TestEnv.Service(out _).CompareAsync("DEV", "QA", null);
        var util = r.Differences.Single(d => d.Name == "ORDUTIL").Id;
        var plan = new DeploymentPlanner().Build(r, [util], "T", "TB1");
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CRTSRVPGM SRVPGM(ORDPGMQA/ORDUTIL)") && s.Command.Contains("EXPORT(*SRCFILE) SRCFILE(ORDSRCQA/QSRVSRC) SRCMBR(ORDUTIL)"));
        Assert.Contains(plan.Steps, s => s.Command.StartsWith("CPYF FROMFILE(ORDSRCDEV/QSRVSRC) TOFILE(ORDSRCQA/QSRVSRC) FROMMBR(ORDUTIL)"));
    }

    [Fact]
    public async Task Adopting_owner_authority_is_flagged()
    {
        var r = await TestEnv.Service(out _).CompareAsync("DEV", "QA", null);
        var entr = r.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == "ORDENTR");
        Assert.Contains(entr.Findings, f => f.Code == "PGM014" && f.Severity == Severity.Warning && f.Message.Contains("OWNER"));
        Assert.Contains(entr.Changes, c => c.Attribute == "Compile options" && c.After!.Contains("USRPRF(*OWNER)"));
    }
}
