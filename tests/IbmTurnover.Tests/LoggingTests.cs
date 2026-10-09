using IbmTurnover.Web.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IbmTurnover.Tests;

public class LoggingTests
{
    private static FileLogSettings Settings(string dir, LogLevel min = LogLevel.Information) => new() { Path = dir, MinLevel = min, Prefix = "t", RetainDays = 2 };

    [Fact]
    public void Entries_at_or_above_the_level_go_to_a_daily_file_with_the_exception()
    {
        var dir = TestEnv.TempDir();
        using (var provider = new FileLoggerProvider(Settings(dir), "C:\\unused"))
        {
            var log = provider.CreateLogger("Cat");
            log.LogDebug("too detailed");
            log.LogWarning("disk {Pct}% full", 91);
            log.LogError(new InvalidOperationException("boom"), "deploy failed");
        }   // Dispose flushes the queue
        var text = File.ReadAllText(Path.Combine(dir, $"t-{DateTime.UtcNow:yyyyMMdd}.log"));
        Assert.DoesNotContain("too detailed", text);
        Assert.Contains("WARN Cat: disk 91% full", text);
        Assert.Contains("ERROR Cat: deploy failed", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
    }

    [Fact]
    public void Old_files_are_pruned_and_a_relative_path_is_under_the_content_root()
    {
        var root = TestEnv.TempDir();
        var dir = Path.Combine(root, "logs");
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "t-20200101.log");
        File.WriteAllText(old, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        using var provider = new FileLoggerProvider(Settings("logs"), root);
        Assert.Equal(dir, provider.LogDirectory);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void A_startup_failure_is_written_to_the_log_folder()
    {
        var dir = TestEnv.TempDir();
        FileLoggerProvider.WriteStartupFailure(Settings(dir), "C:\\unused", new InvalidOperationException("KeyVault:Uri is not an https URL."));
        Assert.Contains("FATAL The app stopped (during startup, or at run time before logging could record it): System.InvalidOperationException: KeyVault:Uri", File.ReadAllText(Directory.GetFiles(dir, "t-startup-*.log").Single()));
    }

    [Fact]
    public void Every_launcher_env_file_has_the_same_keys_and_no_secrets()
    {
        var launcher = Path.Combine(RepoRoot(), "launcher");
        Dictionary<string, string> Read(string name) => File.ReadAllLines(Path.Combine(launcher, name))
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1] : "");
        var local = Read("local.env"); var dev = Read("dev.env"); var prod = Read("prod.env");
        // Same settings in every environment (role groups only exist where Windows login is used).
        foreach (var (a, b) in new[] { (local, dev), (local, prod), (dev, local), (prod, local), (dev, prod), (prod, dev) })
            foreach (var k in a.Keys.Where(k => !k.StartsWith("Turnover__RoleGroups"))) Assert.True(b.ContainsKey(k), $"{k} missing from one env file");
        Assert.Equal("Demo", local["Turnover__Auth__Mode"]);
        Assert.NotEqual("Demo", dev["Turnover__Auth__Mode"]);
        Assert.Equal("Production", prod["ASPNETCORE_ENVIRONMENT"]);
        foreach (var (k, v) in local.Concat(dev).Concat(prod))
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(k + "=" + v, @"(?i)(pwd|password|secret|token|apikey|api_key|accountkey|sharedaccesskey|sig)\s*=\s*\S|UID=\w|-----BEGIN"), $"secret-looking value in {k}");
        foreach (var bat in new[] { "run-local.bat", "run-dev.bat", "run-prod.bat", "_run.bat" }) Assert.True(File.Exists(Path.Combine(launcher, bat)), bat);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "launcher"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root (with launcher/) not found.");
    }
}
