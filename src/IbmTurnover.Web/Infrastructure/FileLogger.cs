using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace IbmTurnover.Web.Infrastructure;

/// <summary>Logging:File settings. Every value comes from configuration (launcher env files, web.config).</summary>
public sealed class FileLogSettings
{
    /// <summary>Folder for the log files; relative paths are under the content root. "none" = file logging off.
    /// (An empty environment variable is removed by the launcher, so web.config's value would apply; use "none".)</summary>
    public string Path { get; set; } = "logs";

    public bool Enabled => !string.IsNullOrWhiteSpace(Path) && !Path.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
    public LogLevel MinLevel { get; set; } = LogLevel.Information;
    /// <summary>Daily files older than this are deleted (at startup and when the day changes; 0 = keep all).</summary>
    public int RetainDays { get; set; } = 30;
    /// <summary>File name prefix: {Prefix}-yyyyMMdd.log and {Prefix}-startup-yyyyMMdd.log.</summary>
    public string Prefix { get; set; } = "turnover";

    /// <summary>Never throws: a malformed Logging:File value (MinLevel=Info, RetainDays=abc) falls back to the defaults,
    /// so a typo in a log setting cannot hide the real startup error.</summary>
    public static FileLogSettings From(IConfiguration configuration)
    {
        try { return configuration.GetSection("Logging:File").Get<FileLogSettings>() ?? new FileLogSettings(); }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Logging:File settings are invalid ({ex.Message}); using defaults.");
            return new FileLogSettings();
        }
    }

    public string Directory(string contentRoot) =>
        System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(contentRoot, Path);
}

/// <summary>
/// Writes log entries to one file per day in the log folder. A background writer drains a queue so requests never wait
/// on disk. Nothing here throws into the application: an unusable folder turns file logging off with a console/stderr
/// warning (see <see cref="Failure"/>), and a full or closed queue drops the entry and counts it.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly string _dir;
    private readonly FileLogSettings _settings;
    private readonly Thread? _writer;
    private readonly Regex _ownFile;
    private bool _reportedFailure;
    private long _dropped;
    private int _pending;
    private DateTime _lastPruneDay;

    public FileLoggerProvider(FileLogSettings settings, string contentRoot)
    {
        _settings = settings;
        _dir = settings.Directory(contentRoot);
        // Only this instance's files: prefix "turnover" must not prune "turnover-dev-*" in a shared folder.
        _ownFile = new Regex($"^{Regex.Escape(settings.Prefix)}-(startup-)?\\d{{8}}\\.log$", RegexOptions.IgnoreCase);
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            // Prove the folder is writable now, not on the first error hours later.
            var probe = System.IO.Path.Combine(_dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Failure = $"File logging is off: the folder {_dir} cannot be written ({ex.Message}). Grant the app pool identity Modify on it, or set Logging:File:Path.";
            Console.Error.WriteLine(Failure);
            _queue.CompleteAdding();
            return;
        }
        Prune();
        _writer = new Thread(Drain) { IsBackground = true, Name = "turnover-file-log" };
        _writer.Start();
    }

    public string LogDirectory => _dir;
    /// <summary>Why file logging is off (null when it works). Program.cs logs it through the other providers.</summary>
    public string? Failure { get; }
    public long Dropped => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool Enabled(LogLevel level) => Failure is null && level != LogLevel.None && level >= _settings.MinLevel;

    internal void Enqueue(string line)
    {
        try
        {
            if (_queue.TryAdd(line)) { Interlocked.Increment(ref _pending); return; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* shutting down */ }
        Interlocked.Increment(ref _dropped);
    }

    /// <summary>Waits until everything queued is on disk (used before the process dies on an unhandled exception).</summary>
    public void Flush(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && sw.Elapsed < timeout && _writer is { IsAlive: true }) Thread.Sleep(10);
    }

    private string FileFor(DateTime utc) => System.IO.Path.Combine(_dir, $"{_settings.Prefix}-{utc:yyyyMMdd}.log");

    private void Drain()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (now.Date != _lastPruneDay) Prune();   // long-running hosts prune when the day changes
                    // Shared write: a second process (or a test host) logging to the same folder must not lock this one out.
                    using var fs = new FileStream(FileFor(now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Interlocked.Increment(ref _dropped);
                    if (!_reportedFailure) { _reportedFailure = true; Console.Error.WriteLine($"File logging to {_dir} failed: {ex.Message}"); }
                }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
        catch (ObjectDisposedException) { /* queue disposed during shutdown */ }
    }

    private void Prune()
    {
        _lastPruneDay = DateTime.UtcNow.Date;
        if (_settings.RetainDays <= 0) return;
        try
        {
            foreach (var f in new DirectoryInfo(_dir).GetFiles("*.log"))
                if (_ownFile.IsMatch(f.Name) && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-_settings.RetainDays)) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not prune old logs in {_dir}: {ex.Message}");
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_queue.IsAddingCompleted && _writer is null) { _queue.Dispose(); return; }
        _queue.CompleteAdding();
        // Only dispose the queue when the writer has finished with it; otherwise leave it to the GC.
        if (_writer is null || _writer.Join(TimeSpan.FromSeconds(5))) _queue.Dispose();
    }

    /// <summary>For failures before logging is configured (bad web.config, missing secret): written straight to the folder.</summary>
    public static void WriteStartupFailure(FileLogSettings settings, string contentRoot, Exception ex)
    {
        try
        {
            var dir = settings.Directory(contentRoot);
            System.IO.Directory.CreateDirectory(dir);
            File.AppendAllText(System.IO.Path.Combine(dir, $"{settings.Prefix}-startup-{DateTime.UtcNow:yyyyMMdd}.log"),
                $"{DateTime.UtcNow:O} FATAL The app stopped (during startup, or at run time before logging could record it): {ex}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not write the startup failure log ({logEx.Message}). Startup failed: {ex}");
        }
    }

    /// <summary>CR/LF in a message would let anyone who controls part of it (a URL, a library name) forge log lines.</summary>
    internal static string OneLine(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => provider.Enabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("O")).Append(' ').Append(Short(logLevel)).Append(' ').Append(category);
            if (eventId.Id != 0) sb.Append('[').Append(eventId.Id).Append(']');
            // The trace id is what the error page shows as "Request id": it ties a user's report to these lines.
            if (Activity.Current?.Id is { } trace) sb.Append(" trace=").Append(trace);
            sb.Append(": ").Append(OneLine(formatter(state, exception)));
            if (exception is not null) sb.AppendLine().Append(exception);
            sb.AppendLine();
            provider.Enqueue(sb.ToString());
        }

        private static string Short(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRACE", LogLevel.Debug => "DEBUG", LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", LogLevel.Critical => "FATAL", _ => l.ToString(),
        };
    }
}
