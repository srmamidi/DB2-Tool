using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Workflow;

/// <summary>One point in an environment's history.</summary>
public sealed class SnapshotRecord
{
    public string Id { get; set; } = "";
    public string Environment { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public string CapturedBy { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Manual, Compare (taken while comparing) or Auto (background schedule).</summary>
    public string Trigger { get; set; } = "Manual";
    public string Fingerprint { get; set; } = "";
    /// <summary>Fingerprint plus data-area values: decides whether a capture needs its own copy (null on older records).</summary>
    public string? ContentKey { get; set; }
    /// <summary>Snapshot whose file holds the data. Equals Id unless the structure was identical to an earlier one.</summary>
    public string PayloadId { get; set; } = "";
    public int Libraries { get; set; }
    public int Objects { get; set; }
    public int Profiles { get; set; }
    /// <summary>Differences against the previous snapshot of the same environment (null for the first).</summary>
    public int? ChangesFromPrevious { get; set; }
    public string? PreviousId { get; set; }
    public bool Unchanged => PayloadId != Id;
}

public interface ISnapshotStore
{
    IReadOnlyList<SnapshotRecord> List(string environment);
    SnapshotRecord? Get(string environment, string id);
    EnvironmentSnapshot Load(string environment, string id);
    SnapshotRecord Save(EnvironmentSnapshot snapshot, Action<SnapshotRecord> describe, int retain);
}

/// <summary>
/// Files under DataDirectory/snapshots/{ENV}: index.json plus one gzip JSON payload per distinct structure.
/// Ids are generated here and validated on every read, so a request can never name a path outside the folder.
/// </summary>
public sealed class FileSnapshotStore(string root) : ISnapshotStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly Regex IdRx = new("^[0-9]{17}-[a-f0-9]{6}$", RegexOptions.CultureInvariant);
    private readonly object _gate = new();

    public static bool IsValidId(string? id) => id is not null && IdRx.IsMatch(id);

    private string Dir(string env)
    {
        if (!TurnoverOptions.IsValidLibraryName(env)) throw new TurnoverException("Invalid environment key.");
        var dir = Path.Combine(root, "snapshots", env.ToUpperInvariant());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private List<SnapshotRecord> Index(string env)
    {
        var file = Path.Combine(Dir(env), "index.json");
        return JsonFiles.ReadOrNew<List<SnapshotRecord>>(file);
    }

    private void WriteIndex(string env, List<SnapshotRecord> list)
    {
        var file = Path.Combine(Dir(env), "index.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    public IReadOnlyList<SnapshotRecord> List(string environment)
    {
        lock (_gate) return Index(environment).OrderByDescending(r => r.CapturedAt).ToList();
    }

    public SnapshotRecord? Get(string environment, string id)
    {
        if (!IsValidId(id)) return null;
        lock (_gate) return Index(environment).FirstOrDefault(r => r.Id == id);
    }

    public EnvironmentSnapshot Load(string environment, string id)
    {
        var rec = Get(environment, id) ?? throw new TurnoverException($"Snapshot {id} not found for {environment}.");
        if (!IsValidId(rec.PayloadId)) throw new TurnoverException("Corrupt snapshot index.");
        var file = Path.Combine(Dir(environment), rec.PayloadId + ".json.gz");
        if (!File.Exists(file)) throw new TurnoverException($"Snapshot data for {id} is missing.");
        using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<EnvironmentSnapshot>(gz, Json) ?? throw new TurnoverException($"Snapshot {id} is unreadable.");
    }

    public SnapshotRecord Save(EnvironmentSnapshot snapshot, Action<SnapshotRecord> describe, int retain)
    {
        var env = snapshot.EnvironmentKey.ToUpperInvariant();
        lock (_gate)
        {
            var list = Index(env);
            var previous = list.OrderByDescending(r => r.CapturedAt).FirstOrDefault();
            var fp = snapshot.Fingerprint();
            var rec = new SnapshotRecord
            {
                Id = $"{snapshot.CapturedAt.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..24],
                Environment = env, CapturedAt = snapshot.CapturedAt, Fingerprint = fp, PreviousId = previous?.Id,
                Libraries = snapshot.Libraries.Count, Profiles = snapshot.Profiles.Count,
                Objects = snapshot.Libraries.Values.Sum(l => l.Tables.Count + l.Views.Count + l.Indexes.Count + l.ForeignKeys.Count + l.Sequences.Count
                    + l.Routines.Count + l.Triggers.Count + l.Programs.Count + l.Aliases.Count + l.DataAreas.Count + l.DataQueues.Count
                    + l.JobDescriptions.Count + l.Messages.Count),
            };
            describe(rec);
            // Data-area values are volatile (left out of the fingerprint) but a value change IS history: dedupe on both.
            rec.ContentKey = fp + ":" + Fingerprints.Of(snapshot.Libraries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(k => k.Value.DataAreas.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => $"{k.Key}/{a.Name}={a.Value}")).ToList());
            if (previous is not null && previous.ContentKey == rec.ContentKey)
            {
                rec.PayloadId = previous.PayloadId;   // same structure: no second copy
                rec.ChangesFromPrevious = 0;
            }
            else
            {
                rec.PayloadId = rec.Id;
                var file = Path.Combine(Dir(env), rec.Id + ".json.gz");
                using (var gz = new GZipStream(File.Create(file + ".tmp"), CompressionLevel.Optimal))
                    JsonSerializer.Serialize(gz, snapshot, Json);
                File.Move(file + ".tmp", file, overwrite: true);
            }
            list.Add(rec);

            // Retention: keep the newest N records, then delete payload files nobody points at.
            list = list.OrderByDescending(r => r.CapturedAt).Take(retain).ToList();
            WriteIndex(env, list);
            var live = list.Select(r => r.PayloadId).ToHashSet();
            foreach (var f in Directory.EnumerateFiles(Dir(env), "*.json.gz"))
                if (!live.Contains(Path.GetFileName(f)[..^".json.gz".Length]))
                    // Housekeeping only: a file another request is reading right now is removed next time.
                    try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return rec;
        }
    }
}

/// <summary>Captures snapshots, keeps the change count against the previous one, and lists history.</summary>
public sealed class SnapshotService(ISnapshotStore store, CatalogService catalog, TurnoverOptions options)
{
    /// <summary>Set by the host: counts differences between two snapshots of one environment.</summary>
    public Func<string, EnvironmentSnapshot, EnvironmentSnapshot, int>? CountChanges { get; set; }
    /// <summary>Set by the host; failures that do not stop a capture are reported here.</summary>
    public Microsoft.Extensions.Logging.ILogger? Log { get; set; }

    public IReadOnlyList<SnapshotRecord> History(string env) => store.List(options.Env(env).Key);
    public SnapshotRecord? Get(string env, string id) => store.Get(options.Env(env).Key, id);
    public EnvironmentSnapshot Load(string env, string id) => store.Load(options.Env(env).Key, id);

    public async Task<SnapshotRecord> CaptureAsync(string envKey, string user, string? label, string trigger, CancellationToken ct = default)
    {
        var env = options.Env(envKey);
        var snap = await catalog.LoadAsync(env, ct, refresh: true, notify: false);
        return Record(snap, user, label, trigger);
    }

    /// <summary>Records a catalog read that already happened (Snapshots:CaptureOnCompare).</summary>
    public SnapshotRecord Record(EnvironmentSnapshot snap, string user, string? label, string trigger)
    {
        var previous = store.List(snap.EnvironmentKey).FirstOrDefault();
        int? changes = null;
        if (previous is not null && previous.Fingerprint != snap.Fingerprint() && CountChanges is not null)
        {
            // The change count is a nicety: a missing or damaged previous payload must not stop history for good.
            try { changes = CountChanges(snap.EnvironmentKey, store.Load(snap.EnvironmentKey, previous.Id), snap); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                changes = null;
                if (Log is not null) Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Log, ex, "Could not count changes since snapshot {Id} of {Env}", previous.Id, snap.EnvironmentKey);
            }
        }
        var rec = store.Save(snap, r =>
        {
            r.CapturedBy = user;
            r.Label = (label ?? "").Trim() is { Length: > 0 } l ? l[..Math.Min(l.Length, 80)]
                : trigger switch { "Manual" => "Manual capture", "Compare" => "Recorded during a comparison", "Auto" => "Scheduled capture", _ => trigger };
            r.Trigger = trigger;
            r.ChangesFromPrevious = changes;
        }, options.Snapshots.RetainPerEnvironment);
        return rec;
    }
}
