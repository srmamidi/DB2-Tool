using System.Text.Json;
using System.Text.RegularExpressions;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Workflow;

public sealed class CheckoutSettings
{
    /// <summary>A package may only carry source changes the requester has checked out.</summary>
    public bool Required { get; set; } = true;
    /// <summary>Check-outs are released when a package deploys successfully to this tier or higher (Dev, QA, Stage, Prod).</summary>
    public EnvironmentTier ReleaseOnTier { get; set; } = EnvironmentTier.Prod;
}

/// <summary>
/// One developer's claim on one source member. The key is LOGICAL (ORDSRC/QRPGLESRC(ORDENTR)), so the lock covers
/// that member in every environment and in every developer library that turns over into it.
/// </summary>
public sealed class Checkout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Key { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Member { get; set; } = "";
    public string User { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Reason { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleasedBy { get; set; }
    public string? ReleaseNote { get; set; }
}

public interface ICheckoutStore
{
    IReadOnlyList<Checkout> All();
    Checkout? Active(string key);
    void Save(Checkout c);
}

public sealed class JsonCheckoutStore : ICheckoutStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<Checkout> _items;

    public JsonCheckoutStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "checkouts.json");
        _items = JsonFiles.ReadOrNew<List<Checkout>>(_path);
    }

    public IReadOnlyList<Checkout> All() { lock (_gate) return _items.Select(Clone).ToList(); }
    public Checkout? Active(string key) { lock (_gate) return _items.FirstOrDefault(c => c.Active && c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } c ? Clone(c) : null; }

    public void Save(Checkout c)
    {
        lock (_gate)
        {
            // Build the new list, write it, and only then make it current: a failed write must not leave memory saying
            // "held" while the file (and the next restart) says "free".
            var next = _items.ToList();
            var i = next.FindIndex(x => x.Id == c.Id);
            if (i < 0) next.Add(Clone(c)); else next[i] = Clone(c);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_path + ".tmp", _path, overwrite: true);
            _items = next;
        }
    }

    private static Checkout Clone(Checkout c) => JsonSerializer.Deserialize<Checkout>(JsonSerializer.Serialize(c))!;
}

/// <summary>A source member a package changes, with its environment-independent lock key.</summary>
public sealed record SourceMember(string DiffId, string Object, string Key, string SourceLibrary, string TargetLibrary, string File, string Member);

public sealed class CheckoutService(ICheckoutStore store, TurnoverOptions options, TimeProvider clock)
{
    private static readonly Regex Name = new("^[A-Z#@$][A-Z0-9_#@$]{0,9}$", RegexOptions.IgnoreCase);
    /// <summary>"Is it held?" and "hold it" must be one step, or two developers can both check out the same member.</summary>
    private static readonly object Gate = new();

    public static string KeyFor(TurnoverEnvironment env, string library, string file, string member) =>
        $"{env.LogicalFor(library) ?? library}/{file}({member})".ToUpperInvariant();

    public IReadOnlyList<Checkout> All() => store.All().OrderByDescending(c => c.Active).ThenByDescending(c => c.At).ToList();
    public Checkout? Holder(string key) => store.Active(key);

    public Checkout CheckOut(TurnoverUser user, string envKey, string library, string file, string member, string? reason)
    {
        if (!user.IsIn(Roles.Developer)) throw new TurnoverException($"{user.Name} needs the Developer role to check out source.");
        var env = options.Env(envKey);
        string lib = library.Trim().ToUpperInvariant(), f = file.Trim().ToUpperInvariant(), m = member.Trim().ToUpperInvariant();
        if (!Name.IsMatch(lib) || !Name.IsMatch(f) || !Name.IsMatch(m)) throw new TurnoverException("Library, file and member must be IBM i names (1-10 characters).");
        var key = KeyFor(env, lib, f, m);
        lock (Gate)
        {
            var held = store.Active(key);
            if (held is not null)
            {
                if (held.User.Equals(user.Name, StringComparison.OrdinalIgnoreCase)) return held;
                throw new TurnoverException($"{key} is checked out by {held.User} since {held.At:yyyy-MM-dd HH:mm} ({held.Reason}).");
            }
            var c = new Checkout { Key = key, Environment = env.Key, Member = $"{lib}/{f}({m})", User = user.Name, At = clock.GetUtcNow(), Reason = (reason ?? "").Trim() };
            store.Save(c);
            return c;
        }
    }

    public Checkout Release(TurnoverUser user, string id, string? note)
    {
        var c = store.All().FirstOrDefault(x => x.Id == id && x.Active) ?? throw new TurnoverException("That check-out is not active.");
        var own = c.User.Equals(user.Name, StringComparison.OrdinalIgnoreCase);
        if (!own && !user.IsIn(Roles.ReleaseManager))
            throw new TurnoverException($"Only {c.User} or a release manager can release this check-out.");
        if (!own && string.IsNullOrWhiteSpace(note)) throw new TurnoverException("Releasing someone else's check-out needs a note.");
        c.Active = false; c.ReleasedAt = clock.GetUtcNow(); c.ReleasedBy = user.Name; c.ReleaseNote = (note ?? "").Trim();
        store.Save(c);
        return c;
    }

    /// <summary>Check out every source member a comparison would change (skips members already held by this user).</summary>
    public (int CheckedOut, List<string> Conflicts) CheckOutAll(TurnoverUser user, ComparisonResult cmp, string reason)
    {
        var done = 0; var conflicts = new List<string>();
        foreach (var m in Members(cmp, cmp.Differences.Select(d => d.Id)))
        lock (Gate)
        {
            var held = store.Active(m.Key);
            if (held is not null && held.User.Equals(user.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (held is not null) { conflicts.Add($"{m.Key} ({held.User})"); continue; }
            // Recorded where the developer works (the comparison's source); the lock itself is the logical key.
            store.Save(new Checkout { Key = m.Key, Environment = cmp.Source.Key, Member = $"{m.SourceLibrary}/{m.File}({m.Member})", User = user.Name, At = clock.GetUtcNow(), Reason = reason });
            done++;
        }
        return (done, conflicts);
    }

    /// <summary>
    /// Source members that the selected differences change (new or changed programs, service programs, DDS files,
    /// commands, menus and panel groups — not level-check recompiles, which rebuild the target's own unchanged source).
    /// </summary>
    public static IReadOnlyList<SourceMember> Members(ComparisonResult cmp, IEnumerable<string> selected)
    {
        var ids = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var map = new LibraryMap(cmp.Pairs, [], cmp.Source, cmp.Target);
        var list = new List<SourceMember>();
        foreach (var d in cmp.Differences.Where(d => ids.Contains(d.Id) && d.Change is ChangeKind.Added or ChangeKind.Modified
                     && d.Kind is ObjectKind.Program or ObjectKind.ServiceProgram or ObjectKind.DdsFile or ObjectKind.Command or ObjectKind.Menu))
        {
            if (d.Source is not ProgramObject p || string.IsNullOrEmpty(p.SourceFile) || string.IsNullOrEmpty(p.SourceLibrary)) continue;
            var member = string.IsNullOrEmpty(p.SourceMember) ? p.Name : p.SourceMember;
            var tgtSrc = cmp.SourceFileLibrary ?? map.MapLibrary(p.SourceLibrary);
            list.Add(new SourceMember(d.Id, d.DisplayName, KeyFor(cmp.Target, tgtSrc, p.SourceFile, member), p.SourceLibrary, tgtSrc, p.SourceFile, member));
        }
        return list;
    }

    /// <summary>Plan findings: someone else holds a member (never overridable), or the requester holds none (when required).</summary>
    public IEnumerable<Finding> Validate(TurnoverPackage p, ComparisonResult cmp)
    {
        foreach (var m in Members(cmp, p.SelectedDiffIds))
        {
            var held = store.Active(m.Key);
            if (held is not null && !held.User.Equals(p.Requester, StringComparison.OrdinalIgnoreCase))
                yield return Finding.Of(Severity.Blocker, "PLAN010", $"{m.Object}: source {m.Key} is checked out by {held.User} ({held.Reason}). Their work in progress would be promoted or overwritten.",
                    $"Wait for {held.User} to finish, or have a release manager release the check-out.");
            else if (held is null && options.Checkout.Required)
                yield return Finding.Of(Severity.Blocker, "PLAN011", $"{m.Object}: source {m.Key} is not checked out by {p.Requester}. Check-out is required before source can be turned over.",
                    "Check it out (Check-outs page, or 'Check out changed members' on the comparison).");
        }
    }

    public int ReleaseForPackage(TurnoverPackage p, ComparisonResult cmp, string by)
    {
        var n = 0;
        foreach (var m in Members(cmp, p.SelectedDiffIds))
            if (store.Active(m.Key) is { } c && c.User.Equals(p.Requester, StringComparison.OrdinalIgnoreCase))
            {
                c.Active = false; c.ReleasedAt = clock.GetUtcNow(); c.ReleasedBy = by; c.ReleaseNote = $"Deployed in {p.Code} to {p.TargetEnv}";
                store.Save(c);
                n++;
            }
        return n;
    }
}
