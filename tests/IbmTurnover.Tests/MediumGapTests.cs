using IbmTurnover.Core;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

/// <summary>The medium-risk gaps: locks, deploy windows and scheduling, pre/post commands, custom steps,
/// distribution to replicas, and the extra object types.</summary>
public class DeployWindowTests
{
    private static readonly DateTime Wed = new(2026, 10, 7, 12, 0, 0);

    [Fact]
    public void Windows_parse_and_cross_midnight()
    {
        Assert.Equal(DayOfWeek.Wednesday, Wed.DayOfWeek);
        var w = DeployWindow.Parse("Sat 22:00-02:00; Daily 03:00-03:30", "PROD");
        Assert.True(DeployWindow.IsOpen(w, new DateTime(2026, 10, 10, 23, 0, 0)));   // Saturday evening
        Assert.True(DeployWindow.IsOpen(w, new DateTime(2026, 10, 11, 1, 59, 0)));   // Sunday morning, same window
        Assert.False(DeployWindow.IsOpen(w, new DateTime(2026, 10, 11, 2, 0, 0)));
        Assert.False(DeployWindow.IsOpen(w, new DateTime(2026, 10, 12, 1, 0, 0)));   // Monday 01:00 is not Saturday's window
        Assert.True(DeployWindow.IsOpen(w, Wed.Date.AddHours(3).AddMinutes(10)));
        Assert.Equal(Wed.Date.AddDays(1).AddHours(3), DeployWindow.NextOpen(w, Wed));
        Assert.True(DeployWindow.IsOpen([], Wed));
        Assert.Throws<TurnoverException>(() => DeployWindow.Parse("Someday 22:00-23:00", "PROD"));
        Assert.Throws<TurnoverException>(() => DeployWindow.Parse("Sat 22:00-22:00", "PROD"));
    }

    [Fact]
    public void Deploy_commands_split_into_command_and_undo()
    {
        var c = DeployCommand.Parse("HLDJOBQ JOBQ(QGPL/ORDJOBQ) => RLSJOBQ JOBQ(QGPL/ORDJOBQ) | SNDMSG MSG('start') TOUSR(QSYSOPR)");
        Assert.Equal(("HLDJOBQ JOBQ(QGPL/ORDJOBQ)", "RLSJOBQ JOBQ(QGPL/ORDJOBQ)"), (c[0].Command, c[0].Undo));
        Assert.Null(c[1].Undo);
    }

    internal static async Task<(TurnoverService Svc, TurnoverPackage P)> ApprovedQaPackage(TurnoverOptions o, TestClock clock, IDeploymentExecutor? executor = null)
    {
        var svc = TestEnv.Service(out _, out _, out _, out _, o, executor, clock: clock);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var col = cmp.Differences.Single(d => d.Kind == ObjectKind.Column && d.Name == "STATUS" && d.Parent == "CUSTOMER").Id;
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [col], "Status column", "CHG-9");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-9.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        return (svc, svc.Package(p.Number));
    }

    [Fact]
    public async Task Outside_the_window_deploy_needs_an_emergency_justification_or_a_schedule()
    {
        var o = TestEnv.Options();
        o.Env("QA").DeployWindows = "Sat 22:00-02:00";
        var clock = new TestClock(new DateTimeOffset(Wed, TimeSpan.Zero));
        var (svc, p) = await ApprovedQaPackage(o, clock);

        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, p.Number));
        Assert.Contains("Sat 2026-10-10 22:00", ex.Message);
        await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, p.Number, emergency: "urgent"));
        Assert.Throws<TurnoverException>(() => svc.Schedule(TestEnv.Release, p.Number, new DateTimeOffset(Wed.AddHours(2), TimeSpan.Zero)));

        var at = new DateTimeOffset(2026, 10, 10, 23, 0, 0, TimeSpan.Zero);
        svc.Schedule(TestEnv.Release, p.Number, at);
        Assert.Equal(0, await svc.RunDueAsync());                                  // not yet
        clock.Now = at.AddMinutes(1);
        Assert.Equal(1, await svc.RunDueAsync());
        var done = svc.Package(p.Number);
        Assert.Equal(PackageStatus.Deployed, done.Status);
        Assert.Null(done.ScheduledFor);
        Assert.Equal("dave", done.Runs.Last().User);
    }

    [Fact]
    public async Task Emergency_deploy_is_recorded()
    {
        var o = TestEnv.Options();
        o.Env("QA").DeployWindows = "Sat 22:00-02:00";
        var (svc, p) = await ApprovedQaPackage(o, new TestClock(new DateTimeOffset(Wed, TimeSpan.Zero)));
        var run = await svc.DeployAsync(TestEnv.Release, p.Number, emergency: "Customer orders are failing since this morning (INC-4411).");
        Assert.True(run.Succeeded);
        Assert.Contains("INC-4411", run.Emergency);
        Assert.Contains(svc.Package(p.Number).Audit, a => a.Action == "Emergency deploy");
    }

    [Fact]
    public async Task A_refused_scheduled_deploy_is_recorded_and_unscheduled()
    {
        var o = TestEnv.Options();
        var clock = new TestClock(new DateTimeOffset(Wed, TimeSpan.Zero));
        var (svc, p) = await ApprovedQaPackage(o, clock);
        svc.Schedule(TestEnv.Release, p.Number, clock.Now.AddHours(1));
        o.Env("QA").DeployWindows = "Sat 22:00-02:00";                          // the window changed after scheduling
        clock.Now = clock.Now.AddHours(2);
        Assert.Equal(0, await svc.RunDueAsync());
        var after = svc.Package(p.Number);
        Assert.Equal(PackageStatus.Approved, after.Status);
        Assert.Null(after.ScheduledFor);
        Assert.Contains(after.Audit, a => a.Action == "Scheduled deploy refused");
    }
}

public class PlanSafetyTests
{
    private static async Task<(ComparisonResult Cmp, string Col)> DevToQa(TurnoverOptions? o = null)
    {
        var cmp = await TestEnv.Service(out _, o).CompareAsync("DEV", "QA", null);
        return (cmp, cmp.Differences.Single(d => d.Kind == ObjectKind.Column && d.Name == "STATUS" && d.Parent == "CUSTOMER").Id);
    }

    [Fact]
    public async Task Tables_changed_in_place_are_checked_for_locks_before_anything_changes()
    {
        var (cmp, col) = await DevToQa();
        var plan = new DeploymentPlanner().Build(cmp, [col], "T", "TB1");
        var lockStep = plan.Steps.Single(s => s.Command.Contains("QSYS2.OBJECT_LOCK_INFO"));
        Assert.Equal(StepPhase.PreCheck, lockStep.Phase);
        Assert.Contains("SYSTEM_OBJECT_SCHEMA = 'CUSQA'", lockStep.Command);
        Assert.True(lockStep.Order < plan.Steps.First(s => s.Phase == StepPhase.Backup).Order);
    }

    [Fact]
    public async Task Pre_deploy_undo_and_post_commands_always_run_even_after_a_failure()
    {
        var o = TestEnv.Options();
        o.Env("QA").PreDeployCommands = "HLDJOBQ JOBQ(QGPL/ORDJOBQ) => RLSJOBQ JOBQ(QGPL/ORDJOBQ)";
        o.Env("QA").PostDeployCommands = "SNDMSG MSG('Turnover finished') TOUSR(QSYSOPR)";
        var (cmp, col) = await DevToQa(o);
        var plan = new DeploymentPlanner().Build(cmp, [col], "T", "TB1");
        Assert.Contains(plan.Steps, s => s.Phase == StepPhase.Setup && s.Command == "HLDJOBQ JOBQ(QGPL/ORDJOBQ)");
        var fin = plan.Steps.Where(s => s.Phase == StepPhase.Finally).ToList();
        Assert.Equal(["RLSJOBQ JOBQ(QGPL/ORDJOBQ)", "SNDMSG MSG('Turnover finished') TOUSR(QSYSOPR)"], fin.Select(s => s.Command));
        Assert.All(fin, s => Assert.True(s.AlwaysRun));
        Assert.Contains("ALWAYS RUNS", DeploymentPlanner.RenderScript(plan, cmp, "T"));

        // The lock check fails (3 jobs hold the table): later changes are skipped, the job queue is still released.
        var fake = new FakeFactory { CheckResult = 3 };
        var run = await new Db2DeploymentExecutor(fake, o).RunAsync(o.Env("QA"), plan.Steps, "Live", "dave");
        Assert.False(run.Succeeded);
        Assert.Contains(fake.Executed, s => s.Contains("RLSJOBQ JOBQ(QGPL/ORDJOBQ)"));
        Assert.DoesNotContain(fake.Executed, s => s.StartsWith("ALTER TABLE"));
        Assert.Equal(StepStatus.Succeeded, run.Results.Single(r => r.Title.Contains("RLSJOBQ")).Status);

        // HLDJOBQ itself fails (someone already held the queue): the release must NOT run, or it would undo their hold.
        var held = new FakeFactory { FailOn = "HLDJOBQ" };
        var run2 = await new Db2DeploymentExecutor(held, o).RunAsync(o.Env("QA"), plan.Steps, "Live", "dave");
        Assert.DoesNotContain(held.Executed, s => s.Contains("RLSJOBQ"));
        Assert.Equal(StepStatus.Skipped, run2.Results.Single(r => r.Title.Contains("RLSJOBQ")).Status);
        Assert.Equal(StepStatus.Succeeded, run2.Results.Single(r => r.Title.Contains("SNDMSG")).Status);
    }

    [Fact]
    public async Task Custom_steps_land_at_their_point_change_the_hash_and_reset_approval()
    {
        var svc = TestEnv.Service(out _);
        var (cmp, col) = await DevToQa();
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [col], "Status", "CHG-10");
        var before = (await svc.AnalyzeAsync(p)).Plan;

        Assert.Throws<TurnoverException>(() => svc.AddCustomStep(TestEnv.Dev, p.Number, CustomStepPoint.AfterTables, StepKind.Sql, "Two", "UPDATE A SET X = 1; DELETE FROM B", null));
        Assert.Throws<TurnoverException>(() => svc.AddCustomStep(TestEnv.Approver1, p.Number, CustomStepPoint.AfterTables, StepKind.Sql, "Not mine", "UPDATE A SET X = 1", null));
        Assert.Throws<TurnoverException>(() => svc.AddCustomStep(TestEnv.Dev, p.Number, CustomStepPoint.AfterAll, StepKind.Check, "Bad check", "DELETE FROM A", null));
        svc.AddCustomStep(TestEnv.Dev, p.Number, CustomStepPoint.AfterTables, StepKind.Sql, "Default status", "UPDATE CUSQA.CUSTOMER SET STATUS = 'A' WHERE STATUS IS NULL", "-- statuses set by the conversion stay");
        svc.AddCustomStep(TestEnv.Dev, p.Number, CustomStepPoint.AfterAll, StepKind.Check, "No customer without status", "SELECT COUNT(*) FROM CUSQA.CUSTOMER WHERE STATUS IS NULL", null);

        var after = (await svc.AnalyzeAsync(svc.Package(p.Number))).Plan;
        Assert.NotEqual(before.ScriptHash, after.ScriptHash);
        var conv = after.Steps.Single(s => s.Title == "Custom: Default status");
        Assert.Equal(StepPhase.Deploy, conv.Phase);
        Assert.True(conv.Order > after.Steps.Where(s => s.Phase == StepPhase.Deploy && !s.Title.StartsWith("Custom")).Max(s => s.Order));
        Assert.Equal(StepKind.Check, after.Steps.Single(s => s.Title == "Custom: No customer without status").Kind);
        Assert.Contains(after.Findings, f => f.Code == "PLAN021");

        var id = svc.Package(p.Number).CustomSteps[0].Id;
        svc.RemoveCustomStep(TestEnv.Dev, p.Number, id);
        Assert.Single(svc.Package(p.Number).CustomSteps);

        // A rejected package that gains a step is back in draft with no decisions on it.
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-10.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, false, "add the default first");
        svc.AddCustomStep(TestEnv.Dev, p.Number, CustomStepPoint.AfterTables, StepKind.Sql, "Default 2", "UPDATE CUSQA.CUSTOMER SET STATUS = 'A' WHERE STATUS = ''", null);
        var reset = svc.Package(p.Number);
        Assert.Equal(PackageStatus.Draft, reset.Status);
        Assert.Empty(reset.Approvals);
    }
}

public class DistributionTests
{
    private static TurnoverOptions WithReplica(DistributionMode mode)
    {
        var o = TestEnv.Options();
        var dr = TestEnv.Env("QADR", EnvironmentTier.QA, "IBMDR02", "QA");
        o.Environments.Add(dr);
        o.Env("QA").DistributionText = $"QADR:{mode}";
        o.Validate();
        return o;
    }

    [Fact]
    public void Deploy_replicas_must_map_libraries_the_same_way()
    {
        var o = TestEnv.Options();
        o.Environments.Add(TestEnv.Env("QADR", EnvironmentTier.QA, "IBMDR02", "QX"));
        o.Env("QA").DistributionText = "QADR:Deploy";
        Assert.Throws<TurnoverException>(() => o.Validate());
    }

    [Fact]
    public async Task The_approved_script_runs_on_deploy_replicas_and_rollback_undoes_both()
    {
        var o = WithReplica(DistributionMode.Deploy);
        var (svc, p) = await DeployWindowTests.ApprovedQaPackage(o, new TestClock(DateTimeOffset.UtcNow));
        Assert.True(p.ApprovedReplicaFingerprints.ContainsKey("QADR"));
        await svc.DeployAsync(TestEnv.Release, p.Number);
        var done = svc.Package(p.Number);
        Assert.Equal(["QA", "QADR"], done.Runs.Where(r => r.Mode == "Simulated").Select(r => r.Environment));
        Assert.Contains(done.Audit, a => a.Action == "Deployed to replica");

        await svc.RollbackAsync(TestEnv.Release, p.Number);
        Assert.Equal(["QA", "QADR"], svc.Package(p.Number).Runs.Where(r => r.Mode == "Rollback").Select(r => r.Environment));
    }

    [Fact]
    public async Task A_replica_added_after_approval_stops_the_deploy()
    {
        var o = TestEnv.Options();
        o.Environments.Add(TestEnv.Env("QADR", EnvironmentTier.QA, "IBMDR02", "QA"));
        o.Validate();
        var (svc, p) = await DeployWindowTests.ApprovedQaPackage(o, new TestClock(DateTimeOffset.UtcNow));
        o.Env("QA").Distribution.Add(new DistributionTarget { Environment = "QADR", Mode = DistributionMode.Deploy });
        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, p.Number));
        Assert.Contains("QADR", ex.Message);
        Assert.Equal(PackageStatus.Approved, svc.Package(p.Number).Status);
    }

    [Fact]
    public async Task Verify_replicas_compares_the_package_objects()
    {
        var o = WithReplica(DistributionMode.Verify);
        var (svc, p) = await DeployWindowTests.ApprovedQaPackage(o, new TestClock(DateTimeOffset.UtcNow));
        await svc.DeployAsync(TestEnv.Release, p.Number);
        Assert.Contains(svc.Package(p.Number).Audit, a => a.Action == "Replicas to verify");
        var run = Assert.Single(await svc.VerifyReplicasAsync(TestEnv.Release, p.Number));
        Assert.Equal(("Verify", "QADR"), (run.Mode, run.Environment));
        Assert.True(run.Succeeded);   // sample QA and QADR are identical
        await Assert.ThrowsAsync<TurnoverException>(() => svc.VerifyReplicasAsync(TestEnv.Dev, p.Number));
    }
}

public class ExtraObjectTypeTests
{
    private static TurnoverOptions Tracked()
    {
        var o = TestEnv.Options();
        o.TrackedAuthorizationLists.Add("ORDAUTL");
        o.TrackedJobSchedules.Add("ORD*");
        o.TrackedSystemValues.AddRange(["QDATFMT", "QMAXSIGN"]);
        foreach (var e in o.Environments) e.IfsPathsText = $"WEB=/www/orders/{e.Key.ToLowerInvariant()}";
        o.Validate();
        return o;
    }

    private static async Task<(ComparisonResult Cmp, DeploymentPlan Plan)> Plan(string from, string to, params ObjectKind[] kinds)
    {
        var cmp = await TestEnv.Service(out _, Tracked()).CompareAsync(from, to, null);
        var ids = cmp.Differences.Where(d => kinds.Contains(d.Kind)).Select(d => d.Id).ToList();
        return (cmp, new DeploymentPlanner().Build(cmp, ids, "T", "TB00000001"));
    }

    [Fact]
    public async Task Menus_panel_groups_and_binding_directories_are_rebuilt()
    {
        var (cmp, plan) = await Plan("DEV", "QA", ObjectKind.Menu, ObjectKind.BindingDirectory);
        Assert.Contains(cmp.Differences, d => d.Kind == ObjectKind.Menu && d.Name == "ORDHLP" && d.Change == ChangeKind.Modified);
        Assert.Contains(plan.Steps, s => s.Command == "CRTPNLGRP PNLGRP(ORDPGMQA/ORDHLP) SRCFILE(ORDSRCQA/QPNLSRC) SRCMBR(ORDHLP) REPLACE(*YES)");
        Assert.Contains(plan.Steps, s => s.Command == "ADDBNDDIRE BNDDIR(ORDPGMQA/ORDBND) OBJ((*LIBL/ORDTAX *MODULE))" && s.Rollback!.Contains("RMVBNDDIRE"));
    }

    [Fact]
    public async Task Authorization_lists_schedules_and_system_values_are_compared_with_mapping()
    {
        var (cmp, plan) = await Plan("DEV", "QA", ObjectKind.AuthorizationList, ObjectKind.JobSchedule, ObjectKind.SystemValue);
        Assert.Contains(plan.Steps, s => s.Command == "ADDAUTLE AUTL(ORDAUTL) USER(ORDSUPV) AUT(*CHANGE)");

        var job = cmp.Differences.Single(d => d.Kind == ObjectKind.JobSchedule);
        Assert.Contains(job.Findings, f => f.Code == "JSE005");                    // held in QA, stays held
        var chg = plan.Steps.Single(s => s.Command.StartsWith("CHGJOBSCDE JOB(ORDNIGHTLY) ENTRYNBR(*ONLY)"));
        Assert.Contains("CMD('CALL PGM(ORDPGMQA/ORDRPT) PARM(''NIGHT'')')", chg.Command);   // library remapped
        Assert.Contains("SCDTIME(233000)", chg.Command);

        var sv = cmp.Differences.Single(d => d.Kind == ObjectKind.SystemValue);
        Assert.Equal("QMAXSIGN", sv.Name);
        Assert.False(sv.DefaultSelected);
        Assert.Contains(sv.Findings, f => f.Code == "SYS002" && f.Severity == Severity.Blocker);
        Assert.DoesNotContain(cmp.RecommendedSelection(), id => id == sv.Id);
    }

    [Fact]
    public async Task Ifs_files_are_copied_with_a_backup_and_secrets_start_unticked()
    {
        var (cmp, plan) = await Plan("DEV", "QA", ObjectKind.IfsFile);
        Assert.Contains(plan.Steps, s => s.Command == "CPY OBJ('/www/orders/qa/conf/app.json') TOOBJ('/www/orders/qa/conf/app.json.TB00000001') REPLACE(*YES)");
        Assert.Contains(plan.Steps, s => s.Command == "CPY OBJ('/www/orders/dev/conf/app.json') TOOBJ('/www/orders/qa/conf/app.json') REPLACE(*YES)");
        Assert.Contains(cmp.Differences, d => d.Kind == ObjectKind.IfsFile && d.Name == "conf/shipping.json" && d.Change == ChangeKind.Added);

        var (prod, _) = await Plan("STAGE", "PROD", ObjectKind.IfsFile);
        var key = prod.Differences.Single(d => d.Name == "conf/server.key");
        Assert.Equal(ChangeKind.Removed, key.Change);
        Assert.False(key.DefaultSelected);
    }

    [Fact]
    public async Task Cross_partition_ifs_copy_streams_chunks()
    {
        var o = Tracked();
        var fake = new FakeFactory();
        var step = new PlanStep { Order = 1, Kind = StepKind.CopyIfs, Title = "copy", SourceEnvironment = "DEV", CopyFrom = "/www/orders/dev/a.json", CopyTo = "/www/orders/prod/a.json", Ccsid = 1208 };
        var run = await new Db2DeploymentExecutor(fake, o).RunAsync(o.Env("PROD"), [step], "Live", "dave");
        Assert.True(run.Succeeded, run.Results[0].Message);
        Assert.Equal(2, fake.Executed.Count(s => s.StartsWith("CALL QSYS2.IFS_WRITE_BINARY")));
        Assert.Contains("5 byte", run.Results[0].Message);

        var bad = new PlanStep { Order = 1, Kind = StepKind.CopyIfs, Title = "copy", SourceEnvironment = "DEV", CopyFrom = "/a/../etc", CopyTo = "/b" };
        var badRun = await new Db2DeploymentExecutor(fake, o).RunAsync(o.Env("PROD"), [bad], "Live", "dave");
        Assert.False(badRun.Succeeded);
        Assert.Contains("Unsafe IFS path", badRun.Results[0].Message);
    }

    [Fact]
    public async Task A_changed_system_object_changes_the_target_fingerprint()
    {
        var o = Tracked();
        var svc = TestEnv.Service(out _, o);
        var a = (await svc.CompareAsync("DEV", "QA", null)).TargetFingerprint;
        o.Env("QA").IfsPaths.Clear();                                                  // QA's IFS files are no longer read
        var b = (await svc.CompareAsync("DEV", "QA", null, refresh: true)).TargetFingerprint;
        Assert.NotEqual(a, b);
    }
}
