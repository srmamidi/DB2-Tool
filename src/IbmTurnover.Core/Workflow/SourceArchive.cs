using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Workflow;

/// <summary>One archived version of one source member in one environment.</summary>
public sealed class SourceVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Environment-independent member key (same as the check-out key).</summary>
    public string Key { get; set; } = "";
    public string Environment { get; set; } = "";
    /// <summary>Physical LIB/FILE(MBR) where the text was read.</summary>
    public string Member { get; set; } = "";
    public string Hash { get; set; } = "";
    public int Lines { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string By { get; set; } = "";
    /// <summary>Before (target at approval), After (source at approval), Deployed, Manual.</summary>
    public string Reason { get; set; } = "";
    public string? Package { get; set; }
}

public interface ISourceArchive
{
    SourceVersion Save(SourceVersion meta, IReadOnlyList<string> lines);
    IReadOnlyList<SourceVersion> Versions(string key);
    IReadOnlyList<SourceVersion> All();
    SourceVersion? Get(string id);
    string Text(SourceVersion v);
}

/// <summary>
/// DataDirectory/source: index.json plus one gzip file per distinct content (named by its SHA-256), so archiving the
/// same text twice costs nothing. Text is only ever read through the index, never from a path built from user input.
/// </summary>
public sealed class FileSourceArchive : ISourceArchive
{
    private readonly string _dir;
    private readonly object _gate = new();
    private List<SourceVersion> _index;

    public FileSourceArchive(string dataDirectory)
    {
        _dir = Path.Combine(dataDirectory, "source");
        Directory.CreateDirectory(_dir);
        var f = Path.Combine(_dir, "index.json");
        _index = File.Exists(f) ? JsonSerializer.Deserialize<List<SourceVersion>>(File.ReadAllText(f)) ?? new() : new();
    }

    public static string HashOf(IReadOnlyList<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines.Select(l => l.TrimEnd())))));

    public SourceVersion Save(SourceVersion meta, IReadOnlyList<string> lines)
    {
        lock (_gate)
        {
            meta.Hash = HashOf(lines);
            meta.Lines = lines.Count;
            var blob = Path.Combine(_dir, meta.Hash + ".txt.gz");
            if (!File.Exists(blob))
            {
                using (var gz = new GZipStream(File.Create(blob + ".tmp"), CompressionLevel.Optimal))
                using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
                    w.Write(string.Join("\n", lines.Select(l => l.TrimEnd())));
                File.Move(blob + ".tmp", blob, overwrite: true);
            }
            // The same text recorded for the same reason and package is one version, not two.
            var dup = _index.FirstOrDefault(v => v.Key == meta.Key && v.Hash == meta.Hash && v.Environment == meta.Environment && v.Reason == meta.Reason && v.Package == meta.Package);
            if (dup is not null) return dup;
            _index.Add(meta);
            var f = Path.Combine(_dir, "index.json");
            File.WriteAllText(f + ".tmp", JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(f + ".tmp", f, overwrite: true);
            return meta;
        }
    }

    public IReadOnlyList<SourceVersion> Versions(string key) { lock (_gate) return _index.Where(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(v => v.CapturedAt).ToList(); }
    public IReadOnlyList<SourceVersion> All() { lock (_gate) return _index.ToList(); }
    public SourceVersion? Get(string id) { lock (_gate) return _index.FirstOrDefault(v => v.Id == id); }

    public string Text(SourceVersion v)
    {
        if (!Regex.IsMatch(v.Hash, "^[0-9A-F]{64}$")) throw new TurnoverException("Corrupt source index.");
        using var gz = new GZipStream(File.OpenRead(Path.Combine(_dir, v.Hash + ".txt.gz")), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadToEnd();
    }
}

/// <summary>Before / after source of one member in a package, for the approver's Source tab.</summary>
public sealed record SourceChange(SourceMember Member, string? Before, string? After, string From);

/// <summary>Reads source from IBM i and archives it at the moments that matter (approval, deployment).</summary>
public sealed class SourceArchiveService(ISourceArchive archive, CatalogService catalog, TurnoverOptions options, TimeProvider clock)
{
    public ISourceArchive Archive => archive;

    /// <summary>Before (target now) and after (source now) text of every member the package changes, read live.</summary>
    public async Task<IReadOnlyList<SourceChange>> LiveChangesAsync(TurnoverPackage p, ComparisonResult cmp, CancellationToken ct)
    {
        var list = new List<SourceChange>();
        var src = options.Env(p.SourceEnv); var tgt = options.Env(p.TargetEnv);
        foreach (var m in CheckoutService.Members(cmp, p.SelectedDiffIds))
        {
            var after = await catalog.Provider.GetSourceAsync(src, m.SourceLibrary, m.File, m.Member, ct);
            var before = await catalog.Provider.GetSourceAsync(tgt, m.TargetLibrary, m.File, m.Member, ct);
            list.Add(new SourceChange(m, before is null ? null : string.Join("\n", before), after is null ? null : string.Join("\n", after), "live"));
        }
        return list;
    }

    /// <summary>The archived before/after of an approved package (what approvers signed).</summary>
    public IReadOnlyList<SourceChange> ArchivedChanges(TurnoverPackage p, ComparisonResult cmp)
    {
        var list = new List<SourceChange>();
        foreach (var m in CheckoutService.Members(cmp, p.SelectedDiffIds))
        {
            var vs = archive.Versions(m.Key).Where(v => v.Package == p.Code).ToList();
            var before = vs.FirstOrDefault(v => v.Reason == "Before");
            var after = vs.FirstOrDefault(v => v.Reason == "After");
            list.Add(new SourceChange(m, before is null ? null : archive.Text(before), after is null ? null : archive.Text(after), "archived at approval"));
        }
        return list;
    }

    /// <summary>Archives what approvers signed: the target's current text (Before) and the incoming text (After).</summary>
    public async Task<int> ArchiveForApprovalAsync(TurnoverPackage p, ComparisonResult cmp, string by, CancellationToken ct)
    {
        var n = 0;
        foreach (var c in await LiveChangesAsync(p, cmp, ct))
        {
            if (c.Before is not null) { Save(c.Member, p.TargetEnv, $"{c.Member.TargetLibrary}/{c.Member.File}({c.Member.Member})", c.Before, by, "Before", p.Code); n++; }
            if (c.After is not null) { Save(c.Member, p.SourceEnv, $"{c.Member.SourceLibrary}/{c.Member.File}({c.Member.Member})", c.After, by, "After", p.Code); n++; }
        }
        return n;
    }

    /// <summary>After a successful deployment: what the target now holds.</summary>
    public async Task<int> ArchiveDeployedAsync(TurnoverPackage p, ComparisonResult cmp, string by, CancellationToken ct)
    {
        var n = 0;
        var tgt = options.Env(p.TargetEnv);
        foreach (var m in CheckoutService.Members(cmp, p.SelectedDiffIds))
            if (await catalog.Provider.GetSourceAsync(tgt, m.TargetLibrary, m.File, m.Member, ct) is { } lines)
            {
                Save(m, p.TargetEnv, $"{m.TargetLibrary}/{m.File}({m.Member})", string.Join("\n", lines), by, "Deployed", p.Code);
                n++;
            }
        return n;
    }

    public async Task<SourceVersion?> CaptureAsync(TurnoverUser user, string envKey, string library, string file, string member, CancellationToken ct)
    {
        // Archived text is visible to every user of the tool: only people who work with source may pull it in.
        if (!user.IsIn(Roles.Developer) && !user.IsIn(Roles.ReleaseManager)) throw new TurnoverException($"{user.Name} needs the Developer or ReleaseManager role to archive source.");
        var env = options.Env(envKey);
        var lines = await catalog.Provider.GetSourceAsync(env, library.ToUpperInvariant(), file.ToUpperInvariant(), member.ToUpperInvariant(), ct);
        if (lines is null) return null;
        var key = CheckoutService.KeyFor(env, library.ToUpperInvariant(), file.ToUpperInvariant(), member.ToUpperInvariant());
        return archive.Save(new SourceVersion
        {
            Key = key, Environment = env.Key, Member = $"{library}/{file}({member})".ToUpperInvariant(), CapturedAt = clock.GetUtcNow(), By = user.Name, Reason = "Manual",
        }, lines);
    }

    private void Save(SourceMember m, string env, string member, string text, string by, string reason, string package) =>
        archive.Save(new SourceVersion { Key = m.Key, Environment = env, Member = member, CapturedAt = clock.GetUtcNow(), By = by, Reason = reason, Package = package },
            text.Split('\n'));
}
