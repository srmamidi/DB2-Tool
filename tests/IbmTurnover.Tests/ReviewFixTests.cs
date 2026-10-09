using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

/// <summary>Regression tests for the defects found in the full code review (one test per confirmed finding).</summary>
public class ReviewWorkflowTests
{
    private static async Task<(TurnoverService Svc, CatalogService Catalog, TurnoverPackage P)> EntryPackage(TurnoverOptions? o = null, IDeploymentExecutor? exec = null)
    {
        o ??= TestEnv.Options();
        var catalog = TestEnv.Catalog(o);
        var svc = TestEnv.Service(out _, out _, out var co, out _, o, exec, catalog);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var entr = cmp.Differences.Single(d => d.Kind == ObjectKind.Program && d.Name == "ORDENTR").Id;
        co.CheckOut(TestEnv.Dev, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDENTR", "CHG-R");
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [entr], "Entry", "CHG-R");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Recompiles of other programs ship separately in CHG-R.");
        return (svc, catalog, svc.Package(p.Number));
    }

    /// <summary>A developer edits the source after approval (the sample catalog hands out its cached objects, so this
    /// is exactly what the next fresh read returns).</summary>
    private static async Task EditDevSource(CatalogService catalog, TurnoverOptions o)
    {
        var lib = await catalog.Provider.GetSnapshotAsync(o.Env("DEV"), "ORDPGMDEV", new CatalogTrace());
        lib!.Programs.Single(x => x.Name == "ORDENTR").SourceHash = "edited-after-approval";
    }

    [Fact]
    public async Task Source_edited_after_approval_is_refused_at_deploy()
    {
        var o = TestEnv.Options();
        var (svc, catalog, p) = await EntryPackage(o);
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        Assert.Contains(svc.Package(p.Number).ApprovedSteps, s => s.Title.StartsWith("Build ORDENTR") && s.Title.Contains(", source "));   // the approved text is pinned in the script
        await EditDevSource(catalog, o);
        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, p.Number));
        Assert.Contains("not the script that was approved", ex.Message);
        Assert.Equal(PackageStatus.Approved, svc.Package(p.Number).Status);
    }

    [Fact]
    public async Task A_script_that_changed_since_submission_resets_the_approvals()
    {
        var o = TestEnv.Options();
        var (svc, catalog, p) = await EntryPackage(o);
        await EditDevSource(catalog, o);
        await Assert.ThrowsAsync<TurnoverException>(() => svc.DecideAsync(TestEnv.Approver1, p.Number, true, "looks fine"));
        var after = svc.Package(p.Number);
        Assert.Equal(PackageStatus.Submitted, after.Status);
        Assert.Empty(after.Approvals);
        Assert.Contains(after.Audit, a => a.Action == "Approvals reset");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "reviewed the new script");   // second look succeeds
        Assert.Equal(PackageStatus.Approved, svc.Package(p.Number).Status);
    }

    [Fact]
    public void A_stale_copy_cannot_overwrite_someone_elses_save()
    {
        var repo = new JsonFileTurnoverRepository(TestEnv.TempDir());
        var p = repo.Create(x => x.Title = "t");
        var a = repo.Get(p.Number)!; var b = repo.Get(p.Number)!;
        a.Status = PackageStatus.Rejected; repo.Save(a);
        b.Status = PackageStatus.Approved;
        Assert.Throws<PackageConflictException>(() => repo.Save(b));
        Assert.Equal(PackageStatus.Rejected, repo.Get(p.Number)!.Status);
        a.Title = "again"; repo.Save(a);                                         // the saver's own copy stays current
    }

    private static async Task<int> ApprovedColumn(TurnoverService svc, string column)
    {
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var id = cmp.Differences.First(d => d.Kind == ObjectKind.Column && d.Name == column).Id;
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [id], column, "CHG-C");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-C.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        return p.Number;
    }

    [Fact]
    public async Task Only_one_deployment_runs_per_environment_and_a_schedule_waits_for_it()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var svc = TestEnv.Service(out var repo, out _, out _, out _, clock: clock);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var cols = cmp.Differences.Where(d => d.Kind == ObjectKind.Column).Select(d => d.Name).Distinct().Take(2).ToList();
        var first = await ApprovedColumn(svc, cols[0]);
        var second = await ApprovedColumn(svc, cols[1]);
        var running = repo.Get(first)!; running.Status = PackageStatus.Deploying; repo.Save(running);

        var ex = await Assert.ThrowsAsync<DeployBusyException>(() => svc.DeployAsync(TestEnv.Release, second));
        Assert.Contains(running.Code, ex.Message);
        svc.Schedule(TestEnv.Release, second, clock.Now.AddMinutes(5));
        clock.Now = clock.Now.AddMinutes(10);
        Assert.Equal(0, await svc.RunDueAsync());
        Assert.NotNull(svc.Package(second).ScheduledFor);                         // still scheduled: tried again next minute
    }

    private sealed class Stub(Func<IReadOnlyList<PlanStep>, CancellationToken, DeploymentRun>? run = null, bool rollbackFails = false) : IDeploymentExecutor
    {
        private readonly SimulatedExecutor _sim = new();
        public bool? LastTokenCancelable;
        public Task<DeploymentRun> RunAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> steps, string mode, string user, CancellationToken ct = default)
        {
            LastTokenCancelable = ct.CanBeCanceled;
            return run is null ? _sim.RunAsync(target, steps, mode, user, ct) : Task.FromResult(run(steps, ct));
        }
        public async Task<DeploymentRun> RollbackAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> completed, string user, CancellationToken ct = default)
        {
            var r = await _sim.RollbackAsync(target, completed, user, ct);
            if (rollbackFails) { r.Succeeded = false; r.Results[0].Status = StepStatus.Failed; }
            return r;
        }
    }

    [Fact]
    public async Task Steps_run_to_the_end_even_if_the_browser_request_is_cancelled()
    {
        var stub = new Stub();
        var svc = TestEnv.Service(out _, out _, out _, out _, executor: stub);
        var n = await ApprovedColumn(svc, (await svc.CompareAsync("DEV", "QA", null)).Differences.First(d => d.Kind == ObjectKind.Column).Name);
        using var cts = new CancellationTokenSource();
        await svc.DeployAsync(TestEnv.Release, n, cts.Token);
        Assert.False(stub.LastTokenCancelable);
    }

    [Fact]
    public async Task An_executor_crash_leaves_the_package_failed_not_stuck_in_deploying()
    {
        var svc = TestEnv.Service(out _, out _, out _, out _, executor: new Stub((_, _) => throw new InvalidOperationException("driver crashed")));
        var n = await ApprovedColumn(svc, (await svc.CompareAsync("DEV", "QA", null)).Differences.First(d => d.Kind == ObjectKind.Column).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeployAsync(TestEnv.Release, n));
        Assert.Equal(PackageStatus.Failed, svc.Package(n).Status);
    }

    [Fact]
    public async Task A_rollback_with_failed_undo_steps_stays_failed_and_can_be_retried()
    {
        var svc = TestEnv.Service(out _, out _, out _, out _, executor: new Stub(rollbackFails: true));
        var n = await ApprovedColumn(svc, (await svc.CompareAsync("DEV", "QA", null)).Differences.First(d => d.Kind == ObjectKind.Column).Name);
        await svc.DeployAsync(TestEnv.Release, n);
        var r = await svc.RollbackAsync(TestEnv.Release, n);
        Assert.False(r.Succeeded);
        Assert.Equal(PackageStatus.Failed, svc.Package(n).Status);
        Assert.Contains(svc.Package(n).Audit, a => a.Action == "Rollback incomplete");
        await svc.RollbackAsync(TestEnv.Release, n);                              // allowed again
    }

    [Fact]
    public async Task A_package_stuck_in_deploying_can_be_marked_failed_by_a_release_manager_only()
    {
        var svc = TestEnv.Service(out var repo);
        var n = await ApprovedColumn(svc, (await svc.CompareAsync("DEV", "QA", null)).Differences.First(d => d.Kind == ObjectKind.Column).Name);
        var p = repo.Get(n)!; p.Status = PackageStatus.Deploying; repo.Save(p);
        Assert.Throws<TurnoverException>(() => svc.MarkFailed(TestEnv.Dev, n, "the app pool recycled during the deploy"));
        Assert.Throws<TurnoverException>(() => svc.MarkFailed(TestEnv.Release, n, "short"));
        Assert.Equal(PackageStatus.Failed, svc.MarkFailed(TestEnv.Release, n, "Checked QA: steps 1-12 applied, nothing running.").Status);
    }

    [Fact]
    public async Task Dry_run_needs_a_developer_or_release_manager()
    {
        var svc = TestEnv.Service(out _);
        var n = await ApprovedColumn(svc, (await svc.CompareAsync("DEV", "QA", null)).Differences.First(d => d.Kind == ObjectKind.Column).Name);
        await Assert.ThrowsAsync<TurnoverException>(() => svc.DryRunAsync(TestEnv.Approver1, n));
        await svc.DryRunAsync(TestEnv.Release, n);
    }

    [Fact]
    public async Task A_stage_replica_that_differs_from_its_primary_blocks_the_approval()
    {
        var o = TestEnv.Options();
        o.Checkout.Required = false;
        // Same libraries as STAGE but Prod-level content: not identical to STAGE.
        o.Environments.Add(TestEnv.Env("STAGEDR", EnvironmentTier.Prod, "IBMDR03", "STG"));
        o.Env("STAGE").DistributionText = "STAGEDR:Deploy";
        o.Validate();
        var svc = TestEnv.Service(out _, o);
        var cmp = await svc.CompareAsync("QA", "STAGE", null);
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "QA", "STAGE", [], [cmp.Differences.First(d => d.Kind == ObjectKind.Column).Id], "x", "CHG-S");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-S.");
        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok"));
        Assert.Contains("STAGEDR", ex.Message);
        Assert.Equal(PackageStatus.Submitted, svc.Package(p.Number).Status);
    }

    [Fact]
    public void Configuration_refuses_a_missing_host_and_replicas_with_different_deploy_commands()
    {
        var o = TestEnv.Options();
        o.Env("QA").Host = "";
        Assert.Contains("Host", Assert.Throws<TurnoverException>(() => o.Validate()).Message);

        var o2 = TestEnv.Options();
        o2.Environments.Add(TestEnv.Env("QADR", EnvironmentTier.QA, "IBMDR02", "QA"));
        o2.Env("QA").PreDeployCommands = "HLDJOBQ JOBQ(QGPL/X) => RLSJOBQ JOBQ(QGPL/X)";
        o2.Env("QA").DistributionText = "QADR:Deploy";
        Assert.Contains("PreDeployCommands", Assert.Throws<TurnoverException>(() => o2.Validate()).Message);
    }

    [Fact]
    public async Task Archiving_source_needs_a_developer_or_release_manager()
    {
        TestEnv.Service(out _, out _, out _, out var archive);
        var o = TestEnv.Options();
        var sources = new SourceArchiveService(archive, TestEnv.Catalog(o), o, TimeProvider.System);
        await Assert.ThrowsAsync<TurnoverException>(() => sources.CaptureAsync(TestEnv.Approver1, "DEV", "ORDSRCDEV", "QRPGLESRC", "ORDENTR", default));
    }

    [Fact]
    public async Task An_unreachable_partition_is_a_failed_run_not_an_exception()
    {
        var o = TestEnv.Options();
        var fake = new FakeFactory { Unreachable = ["QA"], Deploying = true };
        var steps = new[] { new PlanStep { Order = 1, Kind = StepKind.Sql, Title = "a", Command = "ALTER TABLE X ADD COLUMN Y INT" }, new PlanStep { Order = 2, Kind = StepKind.Sql, Title = "b", Command = "DROP INDEX Z" } };
        var run = await new Db2DeploymentExecutor(fake, o).RunAsync(o.Env("QA"), steps, "Live", "dave");
        Assert.False(run.Succeeded);
        Assert.Equal([StepStatus.Failed, StepStatus.Skipped], run.Results.Select(r => r.Status));
        Assert.Contains("Could not connect to QA", run.Results[0].Message);
    }
}

public class ReviewComparerTests
{
    private static readonly TurnoverOptions O = TestEnv.Options();

    private static ComparisonResult Cmp(SchemaSnapshot s, SchemaSnapshot t, SystemObjects? ss = null, SystemObjects? ts = null, TurnoverOptions? o = null)
    {
        o ??= O;
        var src = o.Env("DEV"); var tgt = o.Env("QA");
        s.Library = "ORDDEV"; t.Library = "ORDQA";
        return new SchemaComparer().Compare(new CompareInput
        {
            Source = src, Target = tgt, AllEnvironments = o.Environments, Pairs = LibraryMap.PairsFor(src, tgt, ["ORDERS"]),
            SourceSnapshots = new Dictionary<string, SchemaSnapshot>(StringComparer.OrdinalIgnoreCase) { ["ORDDEV"] = s },
            TargetSnapshots = new Dictionary<string, SchemaSnapshot>(StringComparer.OrdinalIgnoreCase) { ["ORDQA"] = t },
            Map = LibraryMap.ForPromotion(src, tgt, o.Environments), SourceSystem = ss, TargetSystem = ts,
        });
    }

    private static TableDef T1(params string[] cols) => new()
    {
        Name = "T1", SystemName = "T1",
        Columns = cols.Select((c, i) => new ColumnDef { Name = c, SystemName = c, DataType = "INTEGER", Ordinal = i + 1 }).ToList(),
    };

    private static RoutineDef Proc(string lib, string specific, int parms) => new()
    {
        Name = "GETCUST", SpecificName = specific, Parameters = Enumerable.Range(1, parms).Select(i => $"IN P{i} INTEGER").ToList(),
        Definition = $"CREATE OR REPLACE PROCEDURE {lib}.GETCUST ({string.Join(", ", Enumerable.Range(1, parms).Select(i => $"IN P{i} INTEGER"))}) SPECIFIC {lib}.{specific} BEGIN END",
    };

    [Fact]
    public void Two_new_overloads_are_two_changes_and_both_ship()
    {
        var r = Cmp(new SchemaSnapshot { Routines = [Proc("ORDDEV", "GETCUST1", 1), Proc("ORDDEV", "GETCUST2", 2)] }, new SchemaSnapshot());
        var procs = r.Differences.Where(d => d.Kind == ObjectKind.Procedure).ToList();
        Assert.Equal(2, procs.Count);
        var plan = new DeploymentPlanner().Build(r, procs.Select(d => d.Id).ToList(), "T", "B");
        Assert.Contains(plan.Steps, s => s.Command.Contains("SPECIFIC ORDQA.GETCUST1"));
        Assert.Contains(plan.Steps, s => s.Command.Contains("SPECIFIC ORDQA.GETCUST2"));
    }

    [Fact]
    public void A_program_named_like_its_command_still_gets_its_level_check_recompile()
    {
        ProgramObject Cmd(string hash) => new() { Name = "FOO", ObjectType = "*CMD", Attribute = "CMD", SourceLibrary = "SRC", SourceFile = "QCMDSRC", SourceMember = "FOO", SourceHash = hash };
        var pgm = new ProgramObject { Name = "FOO", ObjectType = "*PGM", Attribute = "RPGLE", SourceLibrary = "SRC", SourceFile = "QRPGLESRC", SourceMember = "FOO", SourceHash = "p" };
        pgm.FileReferences.Add(new FileReference("*LIBL", "T1", "X"));
        var r = Cmp(new SchemaSnapshot { Tables = [T1("A", "B")], Programs = [Cmd("new"), pgm] },
                    new SchemaSnapshot { Tables = [T1("A")], Programs = [Cmd("old"), pgm] });
        Assert.Contains(r.Differences, d => d.Kind == ObjectKind.Program && d.Name == "FOO" && d.Findings.Any(f => f.Code == "PGM004"));
    }

    [Fact]
    public void An_index_being_rebuilt_is_not_mistaken_for_the_twin_of_a_new_one()
    {
        IndexDef Ix(string name, string col) => new() { Name = name, SystemName = name, Table = "T1", Columns = [new IndexColumn(col)] };
        var r = Cmp(new SchemaSnapshot { Tables = [T1("A", "B")], Indexes = [Ix("IX_OLD", "B"), Ix("IX_NEW", "A")] },
                    new SchemaSnapshot { Tables = [T1("A", "B")], Indexes = [Ix("IX_OLD", "A")] });
        Assert.Equal(ChangeKind.Added, r.Differences.Single(d => d.Kind == ObjectKind.Index && d.Name == "IX_NEW").Change);
        Assert.Equal(ChangeKind.Modified, r.Differences.Single(d => d.Kind == ObjectKind.Index && d.Name == "IX_OLD").Change);
    }

    [Fact]
    public void Views_and_triggers_that_exist_only_in_the_target_are_listed_and_can_be_dropped()
    {
        var r = Cmp(new SchemaSnapshot { Tables = [T1("A")] }, new SchemaSnapshot
        {
            Tables = [T1("A")],
            Views = [new ViewDef { Name = "V_HOT", SystemName = "V_HOT", Definition = "CREATE VIEW ORDQA.V_HOT AS SELECT A FROM ORDQA.T1" }],
            Triggers = [new TriggerDef { Name = "T1_AUDIT", Table = "T1", Definition = "CREATE TRIGGER ORDQA.T1_AUDIT AFTER INSERT ON ORDQA.T1 FOR EACH ROW BEGIN END" }],
        });
        var view = r.Differences.Single(d => d.Kind == ObjectKind.View);
        var trg = r.Differences.Single(d => d.Kind == ObjectKind.Trigger);
        Assert.Equal((ChangeKind.Removed, false), (view.Change, view.DefaultSelected));
        var plan = new DeploymentPlanner().Build(r, [view.Id, trg.Id], "T", "B");
        Assert.Contains(plan.Steps, s => s.Command == "DROP VIEW ORDQA.V_HOT RESTRICT" && s.Rollback!.StartsWith("CREATE VIEW"));
        Assert.Contains(plan.Steps, s => s.Command == "DROP TRIGGER ORDQA.T1_AUDIT");
    }

    [Fact]
    public void The_fingerprint_does_not_depend_on_catalog_row_order()
    {
        var a = new SchemaSnapshot { Library = "L", Tables = [T1("A"), new TableDef { Name = "T2", SystemName = "T2" }] };
        var b = new SchemaSnapshot { Library = "L", Tables = [new TableDef { Name = "T2", SystemName = "T2" }, T1("A")] };
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Ifs_names_that_differ_only_by_case_do_not_crash_the_comparison()
    {
        var o = TestEnv.Options();
        o.Env("DEV").IfsPathsText = "WEB=/www/dev"; o.Env("QA").IfsPathsText = "WEB=/www/qa";
        o.Validate();
        IfsFileDef F(string rel, string hash) => new() { Logical = "WEB", RelativePath = rel, Size = 1, Hash = hash };
        var r = Cmp(new SchemaSnapshot(), new SchemaSnapshot(), new SystemObjects { IfsFiles = [F("a.txt", "1")] },
            new SystemObjects { IfsFiles = [F("a.txt", "1"), F("A.txt", "2")] }, o);
        Assert.Contains(r.GlobalFindings, f => f.Code == "IFS006");
    }

    [Fact]
    public void A_data_area_value_change_shows_in_history()
    {
        var env = O.Env("DEV");
        EnvironmentSnapshot At(string v) => new()
        {
            EnvironmentKey = "DEV",
            Libraries = { ["ORDPGMDEV"] = new SchemaSnapshot { Library = "ORDPGMDEV", DataAreas = [new DataAreaDef { Name = "ORDCFG", Type = "*CHAR", Length = 1, Value = v }] } },
        };
        var r = TurnoverService.CompareHistory(O, env, At("N"), At("Y"));
        Assert.Contains(r.Differences, d => d.Kind == ObjectKind.DataArea && d.Name == "ORDCFG");
    }

    [Fact]
    public void Optimization_level_40_is_only_passed_to_the_c_compiler()
    {
        ProgramObject P(string attr) => new()
        {
            Name = "P", ObjectType = "*PGM", Attribute = attr, SourceLibrary = "SRC", SourceFile = "QSRC", SourceMember = "P",
            Options = new CompileOptions { ActivationGroup = "*NEW", OptimizationLevel = 40 },
        };
        Assert.All(Builds.Commands(P("RPGLE"), "LIB", "SRC", true, x => x, x => x)!.Where(c => c.Contains("OPTIMIZE")), c => Assert.Contains("OPTIMIZE(*FULL)", c));
        Assert.Contains(Builds.Commands(P("CLE"), "LIB", "SRC", true, x => x, x => x)!, c => c.Contains("OPTIMIZE(40)"));
    }

    [Fact]
    public void A_wider_timestamp_is_widening_not_a_type_family_change()
    {
        Assert.Equal(TypeChange.Widening, TypeCompat.Classify(new ColumnDef { DataType = "TIMESTAMP", Length = 6 }, new ColumnDef { DataType = "TIMESTAMP", Length = 12 }));
        Assert.Equal(TypeChange.Narrowing, TypeCompat.Classify(new ColumnDef { DataType = "TIMESTAMP", Length = 12 }, new ColumnDef { DataType = "TIMESTAMP", Length = 6 }));
    }
}

public class ReviewRound2Tests
{
    [Fact]
    public void A_dropped_column_used_by_a_target_only_view_is_still_blocked()
    {
        var o = TestEnv.Options();
        var src = o.Env("DEV"); var tgt = o.Env("QA");
        TableDef T(params string[] cols) => new() { Name = "T1", SystemName = "T1", Columns = cols.Select((c, i) => new ColumnDef { Name = c, SystemName = c, DataType = "INTEGER", Ordinal = i + 1 }).ToList() };
        var r = new SchemaComparer().Compare(new CompareInput
        {
            Source = src, Target = tgt, AllEnvironments = o.Environments, Pairs = LibraryMap.PairsFor(src, tgt, ["ORDERS"]),
            SourceSnapshots = new Dictionary<string, SchemaSnapshot>(StringComparer.OrdinalIgnoreCase) { ["ORDDEV"] = new() { Library = "ORDDEV", Tables = [T("A")] } },
            TargetSnapshots = new Dictionary<string, SchemaSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["ORDQA"] = new() { Library = "ORDQA", Tables = [T("A", "B")], Views = [new ViewDef { Name = "V", SystemName = "V", Definition = "CREATE VIEW ORDQA.V AS SELECT B FROM ORDQA.T1" }] },
            },
            Map = LibraryMap.ForPromotion(src, tgt, o.Environments),
        });
        var col = r.Differences.Single(d => d.Kind == ObjectKind.Column && d.Name == "B");
        Assert.Contains(col.Findings, f => f.Code == "COL014");                    // not "tick V as well" (which would DROP VIEW in the target)
        Assert.DoesNotContain(r.Differences.Single(d => d.Kind == ObjectKind.View).Id, col.DependsOn);
    }

    [Fact]
    public async Task A_replica_whose_programs_were_rebuilt_at_another_time_is_still_identical()
    {
        var o = TestEnv.Options();
        o.Checkout.Required = false;
        o.Environments.Add(TestEnv.Env("STAGEDR", EnvironmentTier.Stage, "IBMDR03", "STG"));
        o.Env("STAGE").DistributionText = "STAGEDR:Deploy";
        o.Validate();
        var catalog = TestEnv.Catalog(o);
        var svc = TestEnv.Service(out _, out _, out _, out _, o, catalog: catalog);
        var lib = await catalog.Provider.GetSnapshotAsync(o.Env("STAGEDR"), "ORDPGMSTG", new CatalogTrace());
        foreach (var pg in lib!.Programs) pg.Created = pg.Created.AddMinutes(7);   // the DR rebuild ran a few minutes later
        var cmp = await svc.CompareAsync("QA", "STAGE", null);
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "QA", "STAGE", [], [cmp.Differences.First(d => d.Kind == ObjectKind.Column).Id], "x", "CHG-T");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-T.");
        Assert.Equal(PackageStatus.Approved, (await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok")).Status);
    }

    [Fact]
    public async Task A_data_area_value_change_gets_its_own_snapshot()
    {
        var o = TestEnv.Options();
        var catalog = TestEnv.Catalog(o);
        var snaps = new SnapshotService(new FileSnapshotStore(TestEnv.TempDir()), catalog, o);
        var first = await snaps.CaptureAsync("DEV", "t", null, "Manual");
        var lib = await catalog.Provider.GetSnapshotAsync(o.Env("DEV"), "ORDPGMDEV", new CatalogTrace());
        lib!.DataAreas[0].Value = "changed-value";
        var second = await snaps.CaptureAsync("DEV", "t", null, "Manual");
        Assert.NotEqual(first.PayloadId, second.PayloadId);
    }

    private sealed class Gate : IDeploymentExecutor
    {
        private readonly SimulatedExecutor _sim = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RollbackCalls; public List<int> LastRollbackOrders = new(); public bool FailFirstRollback = true;
        public async Task<DeploymentRun> RunAsync(TurnoverEnvironment t, IReadOnlyList<PlanStep> s, string m, string u, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await _sim.RunAsync(t, s, m, u, ct);
        }
        public async Task<DeploymentRun> RollbackAsync(TurnoverEnvironment t, IReadOnlyList<PlanStep> done, string u, CancellationToken ct = default)
        {
            LastRollbackOrders = done.Select(x => x.Order).ToList();
            var r = await _sim.RollbackAsync(t, done, u, ct);
            if (RollbackCalls++ == 0 && FailFirstRollback) { r.Succeeded = false; r.Results[0].Status = StepStatus.Failed; }
            return r;
        }
    }

    private static async Task<int> Approved(TurnoverService svc)
    {
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [cmp.Differences.First(d => d.Kind == ObjectKind.Column).Id], "x", "CHG-G");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-G.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        return p.Number;
    }

    [Fact]
    public async Task A_running_deploy_cannot_be_marked_failed_or_rolled_back()
    {
        var gate = new Gate();
        var svc = TestEnv.Service(out _, out _, out _, out _, executor: gate);
        var n = await Approved(svc);
        var deploy = svc.DeployAsync(TestEnv.Release, n);
        await gate.Entered.Task;
        Assert.Throws<TurnoverException>(() => svc.MarkFailed(TestEnv.Release, n, "Looks stuck, nothing seems to be running."));
        gate.Release.SetResult();
        await deploy;
        Assert.Equal(PackageStatus.Deployed, svc.Package(n).Status);
    }

    [Fact]
    public async Task Retrying_a_rollback_only_runs_the_undos_that_did_not_succeed()
    {
        var gate = new Gate();
        gate.Release.SetResult();
        var svc = TestEnv.Service(out _, out _, out _, out _, executor: gate);
        var n = await Approved(svc);
        await svc.DeployAsync(TestEnv.Release, n);
        await svc.RollbackAsync(TestEnv.Release, n);
        var firstAll = gate.LastRollbackOrders.ToList();
        var failedOrder = svc.Package(n).Runs.Last(r => r.Mode == "Rollback").Results.Single(r => r.Status == StepStatus.Failed).Order;
        await svc.RollbackAsync(TestEnv.Release, n);
        Assert.Equal([failedOrder], gate.LastRollbackOrders);
        Assert.True(firstAll.Count > 1);
        Assert.Equal(PackageStatus.RolledBack, svc.Package(n).Status);
    }
}
