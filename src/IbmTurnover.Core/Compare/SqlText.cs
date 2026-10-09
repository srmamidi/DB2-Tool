using System.Globalization;
using System.Text.RegularExpressions;

namespace IbmTurnover.Core.Compare;

/// <summary>
/// Cheap text helpers. Dependency checks used to build one regex per (object, name) pair, which is
/// O(objects × names) regex compilations on a large library; tokenising each body once makes every
/// "does X mention Y" check a hash lookup.
/// </summary>
public static class SqlText
{
    private static readonly Regex Token = new(@"""(?:[^""]|"""")+""|[A-Za-z_#@$][A-Za-z0-9_#@$]*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every identifier in the text, upper-cased, plus "A.B" and "A/B" for qualified names.
    /// Delimited identifiers keep their exact spelling without the quotes.
    /// </summary>
    public static HashSet<string> Identifiers(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return set;
        string? prev = null; var prevEnd = -1;
        foreach (Match m in Token.Matches(text))
        {
            var id = m.Value[0] == '"' ? m.Value[1..^1].Replace("\"\"", "\"") : m.Value.ToUpperInvariant();
            set.Add(id);
            if (prev is not null)
            {
                var gap = text.AsSpan(prevEnd, m.Index - prevEnd).Trim();
                if (gap.Length == 1 && (gap[0] == '.' || gap[0] == '/')) set.Add(prev + gap[0] + id);
            }
            prev = id; prevEnd = m.Index + m.Length;
        }
        return set;
    }

    /// <summary>
    /// SQL identifier for generated DDL: plain names stay as they are, anything else is delimited ("…") with
    /// embedded quotes doubled, so a catalog name can never break out of the statement.
    /// </summary>
    public static string Id(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new TurnoverException("Empty SQL identifier.");
        return Regex.IsMatch(name, "^[A-Z_#@$][A-Z0-9_#@$]*$") ? name : "\"" + name.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// CL object names are not quoted in generated commands, so only valid system names are accepted.
    /// A catalog row with anything else stops script generation instead of producing a malformed command.
    /// </summary>
    public static string ClName(string name)
    {
        if (name.StartsWith('*') && Regex.IsMatch(name, @"^\*[A-Z]+$")) return name;
        if (!TurnoverOptions.IsValidLibraryName(name))
            throw new TurnoverException($"'{name}' is not a valid IBM i system name; it cannot be used in a CL command.");
        return name.ToUpperInvariant();
    }

    /// <summary>SQL string literal with quotes doubled.</summary>
    public static string Literal(string? value) => "'" + (value ?? "").Replace("'", "''") + "'";

    /// <summary>Text for a CL parameter: quoted with quotes doubled (the whole command is quoted again for QCMDEXC).</summary>
    public static string ClText(string? value) => "'" + (value ?? "").Replace("'", "''") + "'";

    /// <summary>
    /// Numeric catalog values arrive as int, long, decimal, double or string depending on driver and column.
    /// Returns null (never throws) for DBNull, non-numbers and values outside the long range — DECIMAL(31)
    /// sequences and keys can exceed it, and an overflow must not abort a whole catalog read.
    /// </summary>
    public static long? ToLong(object? value)
    {
        switch (value)
        {
            case null or DBNull: return null;
            case long l: return l;
            case int i: return i;
            case short s: return s;
            case decimal d: return d is >= long.MinValue and <= long.MaxValue ? decimal.ToInt64(decimal.Truncate(d)) : null;
            case double f: return f is >= long.MinValue and < 9.2233720368547758E18 && !double.IsNaN(f) ? (long)f : null;
            default:
                return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;
        }
    }

    public static int ToInt(object? value, int fallback = 0) =>
        ToLong(value) is long l && l is >= int.MinValue and <= int.MaxValue ? (int)l : fallback;
}
