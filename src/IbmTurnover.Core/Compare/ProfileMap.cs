using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

/// <summary>
/// User profiles differ per environment (service account SVCORDDEV in Dev is SVCORDQA in QA). Maps names used in
/// authorities, job descriptions and group memberships from the source environment to the target, and flags
/// names that belong to another environment.
/// </summary>
public sealed class ProfileMap
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _foreign = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _tracked;

    /// <summary>(logical or same-name, source profile, target profile) pairs to compare.</summary>
    public IReadOnlyList<(string Logical, string Source, string? Target)> Pairs { get; }

    public ProfileMap(TurnoverEnvironment source, TurnoverEnvironment target, IEnumerable<TurnoverEnvironment> all, IEnumerable<string> tracked)
    {
        _tracked = tracked.ToList();
        var pairs = new List<(string, string, string?)>();
        foreach (var (logical, src) in source.Profiles)
        {
            var tgt = target.PhysicalProfile(logical);
            if (tgt is not null) _map[src] = tgt;
            pairs.Add((logical, src, tgt));
        }
        Pairs = pairs;
        var involved = new HashSet<string>([source.Key, target.Key], StringComparer.OrdinalIgnoreCase);
        foreach (var env in all.Where(e => !involved.Contains(e.Key)))
            foreach (var prof in env.Profiles.Values)
                if (!_map.ContainsKey(prof) && !_map.ContainsValue(prof) && !source.Profiles.ContainsValue(prof) && !target.Profiles.ContainsValue(prof))
                    _foreign.TryAdd(prof, env.Key);
    }

    /// <summary>Identity map for a comparison inside one environment.</summary>
    public static ProfileMap Identity(TurnoverEnvironment env) => new(env, env, [env], []);

    public string Map(string user)
    {
        if (string.IsNullOrEmpty(user) || user.StartsWith('*')) return user;
        return _map.TryGetValue(user, out var t) ? t : user;
    }

    public bool IsForeign(string user, out string environment) => _foreign.TryGetValue(user, out environment!);

    public bool IsTracked(string name) =>
        _tracked.Any(p => p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Profile names the catalog reader must fetch for one environment.</summary>
    public static IReadOnlyList<string> NamesToRead(TurnoverEnvironment env) => env.Profiles.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
