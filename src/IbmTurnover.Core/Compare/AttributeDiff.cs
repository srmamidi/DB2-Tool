using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

/// <summary>
/// Turns each difference into report rows: attribute, value in the target today, value after promotion.
/// One place for every object type, so the report view and the CSV always agree with the cards.
/// </summary>
public static class AttributeDiff
{
    public static void Fill(Difference d)
    {
        if (d.Changes.Count > 0) return;
        var rows = new List<AttributeChange>();
        void Row(string attr, object? before, object? after)
        {
            string? b = Text(before), a = Text(after);
            if (!string.Equals(b, a, StringComparison.Ordinal)) rows.Add(new AttributeChange(attr, b, a));
        }

        if (d.Change == ChangeKind.Added && d.Target is null)
            rows.Add(new AttributeChange("Object", null, "created"));
        else if (d.Change == ChangeKind.Removed)
            rows.Add(new AttributeChange("Object", "exists only here", "(not in source)"));

        switch (d.Source, d.Target)
        {
            case (ColumnDef s, ColumnDef t):
                if (d.Change == ChangeKind.Renamed) Row("Name", t.Name, s.Name);
                Row("Data type", t.TypeSignature, s.TypeSignature);
                Row("Nullable", t.Nullable ? "NULL" : "NOT NULL", s.Nullable ? "NULL" : "NOT NULL");
                Row("Default", t.Default, s.Default);
                Row("CCSID", t.Ccsid, s.Ccsid);
                Row("System name", t.SystemName, s.SystemName);
                Row("Identity", t.IsIdentity, s.IsIdentity);
                Row("Text", t.Text, s.Text);
                Row("Heading", t.Heading, s.Heading);
                if (d.RestartWith is long r) rows.Add(new AttributeChange("Identity restart", t.IdentityNextValue?.ToString(), r.ToString()));
                break;
            case (ColumnDef s, null):
                Row("Data type", null, s.TypeSignature);
                Row("Nullable", null, s.Nullable ? "NULL" : "NOT NULL");
                Row("Default", null, s.Default);
                break;
            case (TableDef s, TableDef t):
                Row("System name", t.SystemName, s.SystemName);
                Row("Journaled", t.Journaled, s.Journaled);
                Row("Text", t.Text, s.Text);
                break;
            case (TableDef s, null):
                Row("Columns", null, s.Columns.Count);
                Row("System name", null, s.SystemName);
                break;
            case (IndexDef s, IndexDef t):
                Row("Name", t.Name, s.Name);
                Row("Keys", Keys(t), Keys(s));
                Row("Unique", Unique(t), Unique(s));
                break;
            case (IndexDef s, null):
                Row("Keys", null, Keys(s));
                Row("Unique", null, Unique(s));
                break;
            case (ForeignKeyDef s, ForeignKeyDef t):
                Row("Name", t.Name, s.Name);
                Row("Columns", string.Join(", ", t.Columns), string.Join(", ", s.Columns));
                Row("References", $"{t.RefLibrary}.{t.RefTable}({string.Join(", ", t.RefColumns)})", $"{s.RefLibrary}.{s.RefTable}({string.Join(", ", s.RefColumns)})");
                Row("On delete", t.DeleteRule, s.DeleteRule);
                Row("On update", t.UpdateRule, s.UpdateRule);
                break;
            case (ForeignKeyDef s, null):
                Row("References", null, $"{s.RefTable}({string.Join(", ", s.RefColumns)})");
                Row("On delete", null, s.DeleteRule);
                break;
            case (SequenceDef s, SequenceDef t):
                Row("Data type", t.DataType, s.DataType);
                Row("Increment", t.Increment, s.Increment);
                Row("Min value", t.MinValue, s.MinValue);
                Row("Max value", t.MaxValue, s.MaxValue);
                Row("Cache", t.Cache, s.Cache);
                Row("Cycle", t.Cycle, s.Cycle);
                if (d.RestartWith is long rs) rows.Add(new AttributeChange("Restart", t.NextValue?.ToString(), rs.ToString()));
                break;
            case (RoutineDef s, RoutineDef t):
                Row("Specific name", t.SpecificName, s.SpecificName);
                Row("Parameters", string.Join(", ", t.Parameters), string.Join(", ", s.Parameters));
                Row("Language", t.Language, s.Language);
                Row("External name", t.ExternalName, d.Details.FirstOrDefault(x => x.StartsWith("EXTERNAL NAME"))?.Split('→').Last().Trim() ?? s.ExternalName);
                Row("Definition", Lines(t.Definition), Changed(t.Definition, d.SourceDdl));
                break;
            case (RoutineDef s, null):
                Row("Parameters", null, string.Join(", ", s.Parameters));
                Row("Language", null, s.Language);
                break;
            case (ViewDef or TriggerDef, not null):
                Row("Definition", Lines(d.TargetDdl), Changed(d.TargetDdl, d.SourceDdl));
                break;
            case (ProgramObject s, ProgramObject t):
                Row("Source", t.SourceHash, s.SourceHash);
                Row("Source changed (UTC)", t.SourceChanged.ToString("yyyy-MM-dd HH:mm"), s.SourceChanged.ToString("yyyy-MM-dd HH:mm"));
                Row("Compiled (UTC)", t.Created.ToString("yyyy-MM-dd HH:mm"), s.Created.ToString("yyyy-MM-dd HH:mm"));
                Row("Attribute", t.Attribute, s.Attribute);
                Row("Compile options", t.Options?.Describe(), s.Options?.Describe());
                if (ReferenceEquals(s, t)) rows.Add(new AttributeChange("Action", null, "recompile for new record format"));
                break;
            case (AliasDef s, AliasDef t):
                Row("Points at", $"{t.BaseLibrary}.{t.BaseTable}", $"{s.BaseLibrary}.{s.BaseTable}");
                Row("Member", t.Member, s.Member);
                break;
            case (DataAreaDef s, DataAreaDef t):
                Row("Type", t.Type, s.Type);
                Row("Length", $"{t.Length},{t.Decimals}", $"{s.Length},{s.Decimals}");
                Row("Text", t.Text, s.Text);
                Row("Value", Masking.DataArea(t.Name, t.Value), Masking.DataArea(s.Name, s.Value));
                break;
            case (DataQueueDef s, DataQueueDef t):
                Row("Max length", t.MaxLength, s.MaxLength);
                Row("Sequence", t.Sequence, s.Sequence);
                Row("Key length", t.KeyLength, s.KeyLength);
                Row("Sender id", t.IncludeSenderId, s.IncludeSenderId);
                Row("Text", t.Text, s.Text);
                break;
            case (JobDescriptionDef s, JobDescriptionDef t):
                Row("Library list", string.Join(" ", t.LibraryList), string.Join(" ", s.LibraryList));
                Row("User", t.User, s.User);
                Row("Job queue", t.JobQueue, s.JobQueue);
                Row("Text", t.Text, s.Text);
                break;
            case (MessageDef s, MessageDef t):
                Row("Text", t.Text, s.Text);
                Row("Second level", t.SecondLevel, s.SecondLevel);
                Row("Severity", t.Severity, s.Severity);
                break;
            case (UserProfileDef s, UserProfileDef t):
                Row("User class", t.UserClass, s.UserClass);
                Row("Status", t.Status, s.Status);
                Row("Special authorities", Join(t.SpecialAuthorities), Join(s.SpecialAuthorities));
                Row("Group profile", t.GroupProfile, s.GroupProfile);
                Row("Supplemental groups", Join(t.SupplementalGroups), Join(s.SupplementalGroups));
                Row("Current library", t.CurrentLibrary, s.CurrentLibrary);
                Row("Initial program", t.InitialProgram, s.InitialProgram);
                Row("Job description", t.JobDescription, s.JobDescription);
                Row("Limit capabilities", t.LimitCapabilities, s.LimitCapabilities);
                Row("Text", t.Text, s.Text);
                break;
            case (UserProfileDef s, null):
                Row("User class", null, s.UserClass);
                Row("Special authorities", null, Join(s.SpecialAuthorities));
                Row("Group profile", null, s.GroupProfile);
                break;
        }
        d.Changes.AddRange(rows);
    }

    private static string? Text(object? v) => v switch
    {
        null => null,
        bool b => b ? "Yes" : "No",
        string s => string.IsNullOrEmpty(s) ? null : s,
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static string Keys(IndexDef i) => string.Join(", ", i.Columns.Select(c => c.Name + (c.Descending ? " DESC" : "")));
    private static string Unique(IndexDef i) => i.UniqueWhereNotNull ? "UNIQUE WHERE NOT NULL" : i.IsUnique ? "UNIQUE" : "No";
    private static string Join(List<string> l) => l.Count == 0 ? "*NONE" : string.Join(" ", l.OrderBy(x => x));
    private static string? Lines(string? s) => s is null ? null : $"{s.Split('\n').Length} lines";

    private static string? Changed(string? before, string? after)
    {
        if (after is null) return null;
        var diff = TextDiff.Compute(before, after);
        int add = diff.Count(x => x.Op == LineOp.Added), del = diff.Count(x => x.Op == LineOp.Removed);
        return $"{after.Split('\n').Length} lines (+{add} / −{del})";
    }
}

/// <summary>
/// Values that may hold secrets (data areas named like *PWD*, *KEY*, connection strings) never leave the server
/// unmasked: not to the browser, not to CSV, not to the audit trail.
/// </summary>
public static class Masking
{
    private static readonly string[] Sensitive = ["PWD", "PASS", "SECRET", "TOKEN", "KEY", "CRED", "AUTH"];

    public static bool IsSensitiveName(string? name) =>
        !string.IsNullOrEmpty(name) && Sensitive.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static string? DataArea(string name, string? value) =>
        value is null ? null : IsSensitiveName(name) || LooksLikeSecret(value) ? "•••••• (masked)" : value;

    /// <summary>"password=…", "pwd=…" or a long unbroken base64/hex run.</summary>
    public static bool LooksLikeSecret(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"(?i)\b(pwd|password|secret|token|apikey)\s*[=:]") ||
        System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^[A-Za-z0-9+/=_\-]{32,}$");
}
