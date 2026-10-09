using System.Collections.Concurrent;
using System.Diagnostics;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Catalog;

public interface ICatalogProvider
{
    /// <summary>
    /// Reads one library. Returns null when the library does not exist. Throws if the library exists
    /// but any part of it cannot be read: a partial snapshot would make missing objects look "removed".
    /// </summary>
    Task<SchemaSnapshot?> GetSnapshotAsync(TurnoverEnvironment env, string library, CatalogTrace trace, CancellationToken ct = default);

    /// <summary>User profiles by exact name or prefix* pattern. Never reads passwords.</summary>
    Task<IReadOnlyList<UserProfileDef>> GetProfilesAsync(TurnoverEnvironment env, IReadOnlyCollection<string> names, IReadOnlyCollection<string> patterns, CatalogTrace trace, CancellationToken ct = default);

    /// <summary>
    /// Authorization lists, job schedule entries and system values named in the Tracked* settings, and every stream file
    /// under the environment's IfsPaths. Throws when any of them cannot be read (fail closed, like libraries).
    /// </summary>
    Task<SystemObjects> GetSystemObjectsAsync(TurnoverEnvironment env, TurnoverOptions options, CatalogTrace trace, CancellationToken ct = default);

    /// <summary>The lines of one source member, or null when the member does not exist.</summary>
    Task<IReadOnlyList<string>?> GetSourceAsync(TurnoverEnvironment env, string library, string file, string member, CancellationToken ct = default);

    /// <summary>Runs each catalog query once and reports what works (used by the Environments page).</summary>
    Task<IReadOnlyList<(string Check, bool Ok, string Message)>> TestAsync(TurnoverEnvironment env, CancellationToken ct = default);
}

/// <summary>One read-only catalog check: what was read, how long it took, and whether it failed.</summary>
public sealed record CatalogCheck(string Group, string Name, bool Ok, long Milliseconds, string? Error, int Rows);

/// <summary>Collects check results from parallel readers (thread-safe).</summary>
public sealed class CatalogTrace
{
    private readonly ConcurrentQueue<CatalogCheck> _checks = new();
    public IReadOnlyList<CatalogCheck> Checks => _checks.ToArray();

    public async Task<T> Run<T>(string group, string name, Func<Task<T>> read, Func<T, int>? rows = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await read();
            _checks.Enqueue(new CatalogCheck(group, name, true, sw.ElapsedMilliseconds, null, rows?.Invoke(result) ?? 0));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _checks.Enqueue(new CatalogCheck(group, name, false, sw.ElapsedMilliseconds, ex.Message, 0));
            throw;
        }
    }

    public Task Run(string group, string name, Func<Task<int>> read) => Run(group, name, read, n => n);
}

/// <summary>What happened the last time an environment was read; drives the Workflow view.</summary>
public sealed record CatalogLoadReport(string Environment, DateTimeOffset At, long Milliseconds, bool FromCache, bool Ok, IReadOnlyList<CatalogCheck> Checks);

public sealed class CatalogLoadException(string message, CatalogLoadReport report) : Exception(message)
{
    public CatalogLoadReport Report { get; } = report;
}

/// <summary>
/// Loads whole environments. Libraries are read in parallel (each on its own connection, failure recorded per
/// check) but the result fails closed: if any library cannot be read the load throws, because comparing a
/// partial environment would report real objects as removed. Results are cached for Catalog:CacheMinutes and
/// concurrent requests for the same environment share one read.
/// </summary>
public sealed class CatalogService(ICatalogProvider provider, TurnoverOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, EnvironmentSnapshot Snap)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CatalogLoadReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after every fresh read (not cache hits), so snapshot history can record it.</summary>
    public event Func<EnvironmentSnapshot, Task>? Loaded;

    public ICatalogProvider Provider => provider;
    public CatalogLoadReport? LastReport(string env) => _reports.GetValueOrDefault(env);
    public IReadOnlyCollection<CatalogLoadReport> Reports => _reports.Values.ToList();

    /// <param name="notify">Raise <see cref="Loaded"/> after a fresh read (snapshot history). A manual capture records its own.</param>
    public async Task<EnvironmentSnapshot> LoadAsync(TurnoverEnvironment env, CancellationToken ct = default, bool refresh = false, bool notify = true)
    {
        var ttl = options.IsLive ? TimeSpan.FromMinutes(options.Catalog.CacheMinutes) : TimeSpan.MaxValue;
        if (!refresh && _cache.TryGetValue(env.Key, out var hit) && clock.GetUtcNow() - hit.At < ttl) return Hit(env, hit.Snap);

        var gate = _locks.GetOrAdd(env.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another request may have finished the read while this one waited.
            if (!refresh && _cache.TryGetValue(env.Key, out hit) && clock.GetUtcNow() - hit.At < ttl) return Hit(env, hit.Snap);
            var snap = await ReadAsync(env, ct);
            _cache[env.Key] = (clock.GetUtcNow(), snap);
            if (notify && Loaded is not null) await Loaded.Invoke(snap);
            return snap;
        }
        finally { gate.Release(); }
    }

    private EnvironmentSnapshot Hit(TurnoverEnvironment env, EnvironmentSnapshot snap)
    {
        if (_reports.TryGetValue(env.Key, out var r)) _reports[env.Key] = r with { FromCache = true };
        return snap;
    }

    private async Task<EnvironmentSnapshot> ReadAsync(TurnoverEnvironment env, CancellationToken ct)
    {
        var trace = new CatalogTrace();
        var started = clock.GetUtcNow();
        var sw = Stopwatch.StartNew();
        var result = new EnvironmentSnapshot { EnvironmentKey = env.Key, CapturedAt = started };
        var failures = new ConcurrentBag<string>();
        using var throttle = new SemaphoreSlim(options.IsLive ? options.Catalog.MaxParallelLibraries : 8);

        var tasks = env.Libraries.Select(async kv =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var snap = await provider.GetSnapshotAsync(env, kv.Value, trace, ct);
                if (snap is null) return;
                snap.Logical = kv.Key;
                lock (result) result.Libraries[kv.Value] = snap;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add($"{kv.Value}: {ex.Message}"); }
            finally { throttle.Release(); }
        }).ToList();

        var profileNames = env.Profiles.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (profileNames.Count > 0 || options.TrackedProfiles.Count > 0)
            tasks.Add(Task.Run(async () =>
            {
                try { result.Profiles = (await provider.GetProfilesAsync(env, profileNames, options.TrackedProfiles, trace, ct)).ToList(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add($"user profiles: {ex.Message}"); }
            }, ct));

        if (env.IfsPaths.Count > 0 || options.TrackedAuthorizationLists.Count > 0 || options.TrackedJobSchedules.Count > 0 || options.TrackedSystemValues.Count > 0)
            tasks.Add(Task.Run(async () =>
            {
                try { result.System = await provider.GetSystemObjectsAsync(env, options, trace, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add($"system objects: {ex.Message}"); }
            }, ct));
        else result.System = new SystemObjects();

        await Task.WhenAll(tasks);
        var report = new CatalogLoadReport(env.Key, started, sw.ElapsedMilliseconds, false, failures.IsEmpty, trace.Checks);
        _reports[env.Key] = report;
        if (!failures.IsEmpty)
            throw new CatalogLoadException($"{env.Key}: {failures.Count} catalog read(s) failed, so nothing is compared (a partial read would show real objects as removed). {string.Join(" | ", failures.Take(3))}", report);
        return result;
    }
}
