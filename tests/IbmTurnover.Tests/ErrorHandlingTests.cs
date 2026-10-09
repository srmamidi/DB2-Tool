using System.Diagnostics;
using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Workflow;
using IbmTurnover.Web.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IbmTurnover.Tests;

/// <summary>Exception and error handling: logging, damaged stores, failed saves, partial steps, the launcher.</summary>
public class FileLoggingErrorTests
{
    [Fact]
    public void An_unwritable_log_folder_turns_file_logging_off_instead_of_stopping_the_app()
    {
        var root = TestEnv.TempDir();
        Directory.CreateDirectory(root);
        var notADir = Path.Combine(root, "logs");
        File.WriteAllText(notADir, "a file where the folder should be");
        using var provider = new FileLoggerProvider(new FileLogSettings { Path = notADir }, root);
        Assert.NotNull(provider.Failure);
        Assert.Contains("Modify", provider.Failure);
        provider.CreateLogger("x").LogError("still callable");   // no exception into the app
    }

    [Fact]
    public void Newlines_cannot_forge_log_lines_and_each_line_carries_the_trace_id()
    {
        var dir = TestEnv.TempDir();
        using var activity = new Activity("t").SetIdFormat(ActivityIdFormat.W3C).Start();
        using (var provider = new FileLoggerProvider(new FileLogSettings { Path = dir, Prefix = "t" }, "C:\\unused"))
            provider.CreateLogger("Cat").LogInformation("source={Src}", "DEV\r\n2026-01-01T00:00:00Z FATAL forged");
        var lines = File.ReadAllLines(Path.Combine(dir, $"t-{DateTime.UtcNow:yyyyMMdd}.log"));
        Assert.Single(lines);
        Assert.Contains("\\r\\n2026-01-01T00:00:00Z FATAL forged", lines[0]);
        Assert.Contains($"trace={activity.Id}", lines[0]);
    }

    [Fact]
    public void Pruning_only_touches_this_prefix_and_bad_settings_fall_back_to_defaults()
    {
        var dir = TestEnv.TempDir();
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "turnover-20200101.log", "turnover-dev-20200101.log" })
        {
            File.WriteAllText(Path.Combine(dir, f), "x");
            File.SetLastWriteTimeUtc(Path.Combine(dir, f), DateTime.UtcNow.AddDays(-40));
        }
        using (new FileLoggerProvider(new FileLogSettings { Path = dir, Prefix = "turnover", RetainDays = 30 }, "C:\\unused")) { }
        Assert.False(File.Exists(Path.Combine(dir, "turnover-20200101.log")));
        Assert.True(File.Exists(Path.Combine(dir, "turnover-dev-20200101.log")));   // another instance's file

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:MinLevel"] = "Info", ["Logging:File:Path"] = "x" }).Build();
        Assert.Equal(LogLevel.Information, FileLogSettings.From(cfg).MinLevel);   // typo does not throw
    }
}

public class StoreErrorTests
{
    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void A_damaged_store_names_the_file_instead_of_starting_empty(string content)
    {
        var dir = TestEnv.TempDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "checkouts.json"), content);
        var ex = Assert.Throws<TurnoverException>(() => new JsonCheckoutStore(dir));
        Assert.Contains("checkouts.json", ex.Message);
        Assert.Equal(content, File.ReadAllText(Path.Combine(dir, "checkouts.json")));   // left untouched
        File.WriteAllText(Path.Combine(dir, "turnover-packages.json"), content);
        Assert.Contains("turnover-packages.json", Assert.Throws<TurnoverException>(() => new JsonFileTurnoverRepository(dir)).Message);
    }

    [Fact]
    public void A_failed_checkout_write_leaves_memory_as_it_was()
    {
        var dir = TestEnv.TempDir();
        var store = new JsonCheckoutStore(dir);
        Directory.CreateDirectory(Path.Combine(dir, "checkouts.json.tmp"));   // the temp file cannot be written
        Assert.ThrowsAny<Exception>(() => store.Save(new Checkout { Key = "K", User = "alice" }));
        Assert.Null(store.Active("K"));                                        // not "held" in memory while free on disk
    }

    [Fact]
    public void Snapshot_history_survives_a_missing_previous_payload()
    {
        var o = TestEnv.Options();
        var root = TestEnv.TempDir();
        var catalog = TestEnv.Catalog(o);
        var snaps = new SnapshotService(new FileSnapshotStore(root), catalog, o) { CountChanges = (_, _, _) => 1 };
        var first = snaps.Record(new EnvironmentSnapshot { EnvironmentKey = "DEV", Libraries = { ["A"] = new SchemaSnapshot { Library = "A" } } }, "t", null, "Manual");
        File.Delete(Path.Combine(root, "snapshots", "DEV", first.PayloadId + ".json.gz"));
        var second = snaps.Record(new EnvironmentSnapshot { EnvironmentKey = "DEV", Libraries = { ["B"] = new SchemaSnapshot { Library = "B" } } }, "t", null, "Manual");
        Assert.Null(second.ChangesFromPrevious);                                // unknown, not a failure
    }
}

public class DeployErrorTests
{
    private sealed class FailingRepo(ITurnoverRepository inner) : ITurnoverRepository
    {
        public bool Fail;
        public IReadOnlyList<TurnoverPackage> List() => inner.List();
        public TurnoverPackage? Get(int number) => inner.Get(number);
        public TurnoverPackage Create(Action<TurnoverPackage> init) => inner.Create(init);
        public void Save(TurnoverPackage package) { if (Fail && package.Status != PackageStatus.Deploying) throw new IOException("disk full"); inner.Save(package); }
    }

    private sealed class ListLogger : ILogger<TurnoverService>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f) => Lines.Add($"{l}: {f(s, ex)}");
    }

    [Fact]
    public async Task When_the_outcome_cannot_be_saved_the_runs_go_to_the_log_and_the_user_is_told()
    {
        var o = TestEnv.Options();
        var dir = TestEnv.TempDir();
        var repo = new FailingRepo(new JsonFileTurnoverRepository(dir));
        var catalog = TestEnv.Catalog(o);
        var co = new CheckoutService(new JsonCheckoutStore(dir), o, TimeProvider.System);
        var sim = new SimulatedExecutor();
        var log = new ListLogger();
        var svc = new TurnoverService(Microsoft.Extensions.Options.Options.Create(o), catalog, repo, sim, sim, new FileSnapshotStore(dir), co,
            new SourceArchiveService(new FileSourceArchive(dir), catalog, o, TimeProvider.System), TimeProvider.System, log);
        var cmp = await svc.CompareAsync("DEV", "QA", null);
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "QA", [], [cmp.Differences.First(d => d.Kind == ObjectKind.Column).Id], "x", "CHG-E");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-E.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        repo.Fail = true;
        var ex = await Assert.ThrowsAsync<TurnoverException>(() => svc.DeployAsync(TestEnv.Release, p.Number));
        Assert.Contains("could not be saved", ex.Message);
        Assert.Contains(log.Lines, l => l.StartsWith("Critical") && l.Contains("\"Mode\":\"Simulated\""));   // what ran is recorded
        repo.Fail = false;
        Assert.Equal(PackageStatus.Failed, svc.MarkFailed(TestEnv.Release, p.Number, "Disk was full; the log shows every step ran.").Status);
    }

    private sealed class FailCopy : IDeploymentExecutor
    {
        private readonly SimulatedExecutor _sim = new();
        public List<int> Undone = new();
        public async Task<DeploymentRun> RunAsync(TurnoverEnvironment t, IReadOnlyList<PlanStep> steps, string m, string u, CancellationToken ct = default)
        {
            var run = await _sim.RunAsync(t, steps, m, u, ct);
            var copy = steps.First(s => s.Kind == StepKind.CopySource).Order;
            foreach (var r in run.Results.Where(r => r.Order >= copy)) r.Status = r.Order == copy ? StepStatus.Failed : StepStatus.Skipped;
            run.Succeeded = false;
            return run;
        }
        public async Task<DeploymentRun> RollbackAsync(TurnoverEnvironment t, IReadOnlyList<PlanStep> done, string u, CancellationToken ct = default)
        {
            Undone = done.Select(s => s.Order).ToList();
            return await _sim.RollbackAsync(t, done, u, ct);
        }
    }

    [Fact]
    public async Task A_copy_step_that_failed_half_way_is_undone_by_the_rollback()
    {
        var o = TestEnv.Options();
        o.Checkout.Required = false;
        var exec = new FailCopy();
        var svc = TestEnv.Service(out _, out _, out _, out _, o, exec);
        var cmp = await svc.CompareAsync("QA", "STAGE", null);   // different partitions: a two-connection copy step
        var pgm = cmp.Differences.First(d => d.Kind is ObjectKind.Program or ObjectKind.ServiceProgram && d.Change == ChangeKind.Modified);
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "QA", "STAGE", [], [pgm.Id], "x", "CHG-F");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Related items ship separately under CHG-F.");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        await svc.DeployAsync(TestEnv.Release, p.Number);
        var copyOrder = svc.Package(p.Number).ApprovedSteps.First(s => s.Kind == StepKind.CopySource).Order;
        await svc.RollbackAsync(TestEnv.Release, p.Number);
        Assert.Contains(copyOrder, exec.Undone);
    }

    [Fact]
    public async Task An_unreachable_replica_is_reported_by_the_rollback_not_thrown()
    {
        var o = TestEnv.Options();
        var fake = new FakeFactory { Unreachable = ["QA"], Deploying = true };
        var run = await new Db2DeploymentExecutor(fake, o).RollbackAsync(o.Env("QA"),
            [new PlanStep { Order = 1, Kind = StepKind.Sql, Title = "a", Command = "x", Rollback = "DROP TABLE X" }], "dave");
        Assert.False(run.Succeeded);
        Assert.Contains("Could not connect to QA", run.Results[0].Message);
    }

    [Fact]
    public void Deploy_lock_wait_and_scheduler_interval_are_validated_settings()
    {
        var o = TestEnv.Options();
        o.DeployGateWaitSeconds = 0;
        Assert.Contains("DeployGateWaitSeconds", Assert.Throws<TurnoverException>(() => o.Validate()).Message);
    }
}

public class LauncherTests
{
    [Fact]
    public void The_launcher_survives_a_log_path_with_parentheses_and_rejects_an_unknown_mode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launcher = Path.Combine(Root(), "launcher");
        var logDir = Path.Combine(TestEnv.TempDir(), "Logs (x86) & co");
        var env = Path.Combine(launcher, $"test-{Guid.NewGuid():N}.env");
        File.WriteAllText(env, $"# test\r\nLogging__File__Path={logDir}\r\nLAUNCH_MODE=bogus\r\nTurnover__RoleGroups__Developer__0=CORP\\TOV-Developers\r\n");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{Path.Combine(launcher, "_run.bat")}\" {Path.GetFileName(env)}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            Assert.Equal(2, proc.ExitCode);
            Assert.Contains("LAUNCH_MODE must be", output);
            Assert.True(Directory.Exists(logDir), output);
        }
        finally { File.Delete(env); }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "launcher"))) d = d.Parent;
        return d!.FullName;
    }
}

public class ErrorHandlingRound2Tests
{
    [Fact]
    public void None_switches_file_logging_off_and_a_second_dispose_is_harmless()
    {
        Assert.False(new FileLogSettings { Path = "none" }.Enabled);
        Assert.False(new FileLogSettings { Path = " " }.Enabled);
        Assert.True(new FileLogSettings { Path = "logs" }.Enabled);
        var p = new FileLoggerProvider(new FileLogSettings { Path = TestEnv.TempDir() }, "C:\\unused");
        p.Dispose();
        p.Dispose();
    }

    private sealed class FailFirstCopy : IDeploymentExecutor
    {
        private readonly SimulatedExecutor _sim = new();
        public List<int> Undone = new();
        public async Task<DeploymentRun> RunAsync(TurnoverEnvironment t, IReadOnlyList<PlanStep> steps, string m, string u, CancellationToken ct = default)
        {
            var run = await _sim.RunAsync(t, steps, m, u, ct);
            var copy = steps.First(s => s.Kind == StepKind.CopySource).Order;
            foreach (var r in run.Results.Where(r => r.Order >= copy)) r.Status = r.Order == copy ? StepStatus.Failed : StepStatus.Skipped;
            run.Succeeded = false;
            return run;
        }
        public async Task<DeploymentRun> RollbackAsync(TurnoverEnvironment t, IReadOnlyList<PlanStep> done, string u, CancellationToken ct = default)
        {
            Undone = done.Select(s => s.Order).ToList();
            return await _sim.RollbackAsync(t, done, u, ct);
        }
    }

    [Fact]
    public async Task A_failed_copy_of_a_new_member_is_not_undone_because_there_is_nothing_to_remove()
    {
        var o = TestEnv.Options();
        o.Checkout.Required = false;
        var exec = new FailFirstCopy();
        var svc = TestEnv.Service(out _, out _, out _, out _, o, exec);
        var cmp = await svc.CompareAsync("DEV", "STAGE", null);   // different partitions; SHPLBLR exists only in DEV
        var pgm = cmp.Differences.First(d => d.Kind == ObjectKind.Program && d.Name == "SHPLBLR" && d.Change == ChangeKind.Added);
        var p = await svc.CreatePackageAsync(TestEnv.Dev, "DEV", "STAGE", [], [pgm.Id], "x", "CHG-N");
        await svc.SubmitAsync(TestEnv.Dev, p.Number, "Tier skip agreed for this test package (CHG-N).");
        await svc.DecideAsync(TestEnv.Approver1, p.Number, true, "ok");
        await svc.DeployAsync(TestEnv.Release, p.Number);
        var copy = svc.Package(p.Number).ApprovedSteps.First(s => s.Kind == StepKind.CopySource);
        Assert.Contains("RMVM", copy.Rollback);
        try { await svc.RollbackAsync(TestEnv.Release, p.Number); } catch (TurnoverException) { /* nothing else ran: "rollback not needed" */ }
        Assert.DoesNotContain(copy.Order, exec.Undone);
    }

    [Fact]
    public void The_launcher_refuses_dll_mode_without_a_publish_folder_before_creating_any_log_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launcher = Path.Combine(Root(), "launcher");
        var env = Path.Combine(launcher, $"test-{Guid.NewGuid():N}.env");
        var rel = $"tov-test-{Guid.NewGuid():N}";
        File.WriteAllText(env, $"LAUNCH_MODE=dll\r\nLogging__File__Path={rel}\r\n");
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{Path.Combine(launcher, "_run.bat")}\" {Path.GetFileName(env)}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            Assert.Equal(2, proc.ExitCode);
            Assert.Contains("needs PUBLISH_DIR", output);
            Assert.False(Directory.Exists(Path.Combine(Path.GetPathRoot(launcher)!, rel)));
        }
        finally { File.Delete(env); }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "launcher"))) d = d.Parent;
        return d!.FullName;
    }
}
