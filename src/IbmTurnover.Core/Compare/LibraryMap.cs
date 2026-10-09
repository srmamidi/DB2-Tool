using System.Text;
using System.Text.RegularExpressions;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

/// <summary>One rewritten library reference. <c>Object</c> is the name after LIB. or LIB/ when there is one.</summary>
public sealed record LibraryRewrite(string From, string To, int Line, bool InStringLiteral, bool InExternalName = false, string? Object = null);
public sealed record ForeignReference(string Library, string OwnerEnvironment, int Line);

public sealed record RemapResult(string Text, IReadOnlyList<LibraryRewrite> Rewrites, IReadOnlyList<ForeignReference> Foreign);

/// <summary>
/// Translates physical library names from the source environment to the target environment
/// (ORDDEV → ORDQA) inside DDL, routine bodies, EXTERNAL NAME clauses and CL commands,
/// and reports references to libraries that belong to some OTHER environment (a Dev procedure
/// that reads ORDPRD is a defect, not something to rewrite).
/// </summary>
public sealed class LibraryMap
{
    private readonly Dictionary<string, string> _map;
    private readonly Dictionary<string, string> _foreign; // library -> owning env
    private readonly Regex? _sourceRx;
    private readonly Regex? _foreignRx;

    public IReadOnlyDictionary<string, string> SourceToTarget => _map;
    public IReadOnlySet<string> SourceLibraries { get; }
    public IReadOnlySet<string> TargetLibraries { get; }
    public IReadOnlyDictionary<string, string> ForeignLibraries => _foreign;

    public LibraryMap(IEnumerable<LibraryPair> pairs, IEnumerable<TurnoverEnvironment> allEnvs, params TurnoverEnvironment[] involved)
    {
        _map = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pairs) _map[p.SourceLibrary] = p.TargetLibrary;
        SourceLibraries = new HashSet<string>(_map.Keys, StringComparer.OrdinalIgnoreCase);
        TargetLibraries = new HashSet<string>(_map.Values, StringComparer.OrdinalIgnoreCase);

        var involvedKeys = new HashSet<string>(involved.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        _foreign = new(StringComparer.OrdinalIgnoreCase);
        foreach (var env in allEnvs.Where(e => !involvedKeys.Contains(e.Key)))
            foreach (var lib in env.Libraries.Values)
                if (!_map.ContainsKey(lib) && !TargetLibraries.Contains(lib))
                    _foreign.TryAdd(lib, env.Key);

        _sourceRx = BuildRx(_map.Keys.Where(k => !k.Equals(_map[k], StringComparison.OrdinalIgnoreCase)));
        _foreignRx = BuildRx(_foreign.Keys);
    }

    /// <summary>Promotion map: every logical library mapped in both environments.</summary>
    public static LibraryMap ForPromotion(TurnoverEnvironment source, TurnoverEnvironment target, IEnumerable<TurnoverEnvironment> allEnvs) =>
        new(PairsFor(source, target, source.Libraries.Keys), allEnvs, source, target);

    public static List<LibraryPair> PairsFor(TurnoverEnvironment source, TurnoverEnvironment target, IEnumerable<string> logicals) =>
        logicals.Where(l => source.PhysicalFor(l) is not null && target.PhysicalFor(l) is not null)
                .Select(l => new LibraryPair(l.ToUpperInvariant(), source.PhysicalFor(l)!, target.PhysicalFor(l)!))
                .ToList();

    public string MapLibrary(string lib) => _map.TryGetValue(lib, out var t) ? t : lib;
    public bool IsMapped(string lib) => _map.ContainsKey(lib);

    /// <summary>Rewrites 'LIB/OBJ' or 'LIB/OBJ(PROC)' style names. *LIBL and *CURLIB are left alone.</summary>
    public string MapQualifiedObject(string qualified)
    {
        var slash = qualified.IndexOf('/');
        if (slash <= 0) return qualified;
        var lib = qualified[..slash];
        return lib.StartsWith('*') ? qualified : MapLibrary(lib) + qualified[slash..];
    }

    public RemapResult Remap(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new RemapResult(text ?? "", [], []);
        var literal = LiteralMask(text);
        var lines = LineStarts(text);
        int LineOf(int index) { var i = lines.BinarySearch(index); return (i >= 0 ? i : ~i - 1) + 1; }
        var rewrites = new List<LibraryRewrite>();
        var foreign = new List<ForeignReference>();

        if (_foreignRx is not null)
            foreach (Match m in _foreignRx.Matches(text))
                foreign.Add(new ForeignReference(m.Value.ToUpperInvariant(), _foreign[m.Value], LineOf(m.Index)));

        if (_sourceRx is null) return new RemapResult(text, rewrites, foreign);

        var result = _sourceRx.Replace(text, m =>
        {
            var to = _map[m.Value];
            var after = ObjectAfter.Match(text, m.Index + m.Length);
            // Look back a short window only: slicing text[..index] per match made long bodies O(n²).
            var start = Math.Max(0, m.Index - 48);
            var inExternal = ExternalBefore.IsMatch(text.AsSpan(start, m.Index - start).ToString());
            rewrites.Add(new LibraryRewrite(m.Value.ToUpperInvariant(), to, LineOf(m.Index), literal[m.Index], inExternal,
                after.Success ? after.Groups[1].Value.ToUpperInvariant() : null));
            return to;
        });
        return new RemapResult(result, rewrites, foreign);
    }

    private static readonly Regex ObjectAfter = new(@"\G\s*[./]\s*""?([A-Za-z0-9_#@$]+)", RegexOptions.CultureInvariant);
    private static readonly Regex ExternalBefore = new(@"EXTERNAL\s+NAME\s+'?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.RightToLeft);

    private static Regex? BuildRx(IEnumerable<string> libs)
    {
        var list = libs.OrderByDescending(l => l.Length).Select(Regex.Escape).ToList();
        if (list.Count == 0) return null;
        // IBM i names may contain # @ $; treat them as word characters so ORDDEV never matches inside ORDDEV2.
        return new Regex($@"(?<![A-Za-z0-9_#@$])(?:{string.Join("|", list)})(?![A-Za-z0-9_#@$])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    private static bool[] LiteralMask(string text)
    {
        var mask = new bool[text.Length];
        var inside = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'') inside = !inside; // '' (escaped quote) toggles twice: correct.
            mask[i] = inside;
        }
        return mask;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    public static string Normalize(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "";
        var sb = new StringBuilder(sql.Length);
        var space = false;
        var literal = false;
        foreach (var ch in sql.Trim())
        {
            if (ch == '\'') literal = !literal;
            // Whitespace and case are cosmetic in SQL, but not inside a string literal ('a' <> 'A').
            if (!literal && char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(literal ? ch : char.ToUpperInvariant(ch));
        }
        return sb.ToString().TrimEnd(';', ' ');
    }
}
