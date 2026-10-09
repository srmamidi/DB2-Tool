using System.Text;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

/// <summary>One row of the change report: one attribute of one object.</summary>
public sealed record ChangeReportRow(
    string Kind, string Library, string Object, string Change, string Severity, string Attribute, string? Before, string? After, string Rules, string Id);

/// <summary>Flat, attribute-level view of a comparison for the report screen and CSV/Excel export.</summary>
public static class ChangeReport
{
    public static IReadOnlyList<ChangeReportRow> Rows(ComparisonResult r) =>
        r.Differences.SelectMany(d =>
        {
            var rules = string.Join(" ", d.Findings.Select(f => f.Code).Distinct());
            var lib = d.Logical == "*SYSTEM" ? "*SYSTEM" : d.TargetLibrary;
            var changes = d.Changes.Count > 0 ? d.Changes : [new AttributeChange("Object", null, string.Join("; ", d.Details))];
            return changes.Select(c => new ChangeReportRow(d.Kind.ToString(), lib, d.DisplayName, d.Change.ToString(), d.Severity.ToString(),
                c.Attribute, c.Before, c.After, rules, d.Id));
        }).ToList();

    /// <summary>
    /// RFC 4180 CSV with a UTF-8 BOM (Excel). Cells that start with = + - @ tab or CR are prefixed with an apostrophe:
    /// object text comes from the catalog and must not become a spreadsheet formula (CSV injection).
    /// </summary>
    public static byte[] Csv(ComparisonResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Source.Key} ({r.SourceLabel}) -> {r.Target.Key} ({r.TargetLabel}), compared {r.ComparedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine("Type,Library,Object,Change,Severity,Attribute,Target now,After promotion,Rules");
        foreach (var row in Rows(r))
            sb.AppendLine(string.Join(",", new[] { row.Kind, row.Library, row.Object, row.Change, row.Severity, row.Attribute, row.Before, row.After, row.Rules }.Select(Cell)));
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return [.. preamble, .. body];
    }

    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
