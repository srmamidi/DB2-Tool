using IbmTurnover.Core.Models;
using Microsoft.AspNetCore.Html;

namespace IbmTurnover.Web.Infrastructure;

/// <summary>Small presentation helpers shared by the views.</summary>
public static class Ui
{
    public static string Sev(Severity s) => s switch { Severity.Blocker => "blocker", Severity.Warning => "warning", _ => "info" };
    public static string SevLabel(Severity s) => s switch { Severity.Blocker => "Blocker", Severity.Warning => "Warning", _ => "Info" };

    public static string Change(ChangeKind c) => c switch
    {
        ChangeKind.Added => "added", ChangeKind.Removed => "removed", ChangeKind.Renamed => "renamed", ChangeKind.Drift => "drift", _ => "modified",
    };

    public static string KindLabel(ObjectKind k) => k switch
    {
        ObjectKind.ForeignKey => "Foreign keys", ObjectKind.ServiceProgram => "Service programs", ObjectKind.Library => "Libraries",
        ObjectKind.Index => "Indexes", ObjectKind.Sequence => "Sequences", ObjectKind.Column => "Columns", ObjectKind.Table => "Tables",
        ObjectKind.View => "Views", ObjectKind.Function => "Functions", ObjectKind.Procedure => "Stored procedures", ObjectKind.Trigger => "Triggers",
        ObjectKind.Program => "RPG / CL programs", ObjectKind.CheckConstraint => "Check constraints", ObjectKind.Alias => "Aliases",
        ObjectKind.DdsFile => "Display / printer / DDS files", ObjectKind.Command => "Commands", ObjectKind.DataArea => "Data areas",
        ObjectKind.DataQueue => "Data queues", ObjectKind.Message => "Messages", ObjectKind.JobDescription => "Job descriptions",
        ObjectKind.UserProfile => "User profiles & service accounts", ObjectKind.Authority => "Object authorities & owners",
        ObjectKind.Menu => "Menus & panel groups", ObjectKind.BindingDirectory => "Binding directories", ObjectKind.AuthorizationList => "Authorization lists",
        ObjectKind.JobSchedule => "Job schedule entries", ObjectKind.SystemValue => "System values", ObjectKind.IfsFile => "IFS stream files",
        _ => k.ToString(),
    };

    public static string Tier(EnvironmentTier t) => t.ToString().ToLowerInvariant();

    public static string Status(PackageStatus s) => s switch
    {
        PackageStatus.Approved or PackageStatus.Deployed => "good",
        PackageStatus.Rejected or PackageStatus.Failed => "danger",
        PackageStatus.Submitted or PackageStatus.Deploying or PackageStatus.RollingBack => "warn",
        PackageStatus.RolledBack => "muted",
        _ => "info",
    };

    public static string Phase(StepPhase p) => p.ToString().ToLowerInvariant();

    public static string RunStatus(StepStatus s) => s switch
    {
        StepStatus.Succeeded => "good", StepStatus.Failed => "danger", StepStatus.Skipped => "muted", _ => "info",
    };

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["home"] = "M3 11.5 12 4l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z",
        ["compare"] = "M7 4v13m0 0-3-3m3 3 3-3M17 20V7m0 0-3 3m3-3 3 3",
        ["package"] = "M4 7.5 12 3l8 4.5v9L12 21l-8-4.5zM4 7.5l8 4.5 8-4.5M12 12v9",
        ["env"] = "M4 5h16v5H4zM4 14h16v5H4zM8 7.5h.01M8 16.5h.01",
        ["move"] = "M4 7h11m0 0-3-3m3 3-3 3M20 17H9m0 0 3-3m-3 3 3 3",
        ["sun"] = "M12 4V2m0 20v-2m8-8h2M2 12h2m13.66-5.66 1.41-1.41M4.93 19.07l1.41-1.41m0-11.32L4.93 4.93m14.14 14.14-1.41-1.41M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10z",
        ["moon"] = "M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5z",
        ["check"] = "M5 12.5 10 17 19 7",
        ["alert"] = "M12 9v4m0 4h.01M10.3 3.9 2.4 18a2 2 0 0 0 1.7 3h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z",
        ["info"] = "M12 8h.01M11 12h1v5h1M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z",
        ["arrow"] = "M5 12h14m0 0-5-5m5 5-5 5",
        ["copy"] = "M9 9h10v12H9zM5 15V3h10",
        ["download"] = "M12 3v12m0 0-4-4m4 4 4-4M4 19h16",
        ["shield"] = "M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6z",
        ["db"] = "M4 6c0-1.7 3.6-3 8-3s8 1.3 8 3-3.6 3-8 3-8-1.3-8-3zm0 0v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3",
        ["swap"] = "M7 4 3 8l4 4M3 8h13M17 20l4-4-4-4m4 4H8",
        ["play"] = "M7 4v16l13-8z",
        ["undo"] = "M9 14 4 9l5-5M4 9h11a5 5 0 0 1 0 10h-3",
        ["user"] = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm-8 9a8 8 0 0 1 16 0",
        ["flow"] = "M4 5h6v4H4zM14 15h6v4h-6zM7 9v3a3 3 0 0 0 3 3h4",
        ["search"] = "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm9 16-4.3-4.3",
    };

    public sealed record Row(int? LeftNo, string? Left, string LeftCls, int? RightNo, string? Right, string RightCls);

    /// <summary>Side-by-side rows: a run of removed lines is paired with the run of added lines that follows it.</summary>
    public static List<Row> Align(string? left, string? right)
    {
        var lines = IbmTurnover.Core.Compare.TextDiff.Compute(left, right);
        var rows = new List<Row>();
        for (var i = 0; i < lines.Count;)
        {
            if (lines[i].Op == IbmTurnover.Core.Compare.LineOp.Same)
            {
                rows.Add(new Row(lines[i].LeftNo, lines[i].Left, "", lines[i].RightNo, lines[i].Right, ""));
                i++;
                continue;
            }
            var removed = new List<IbmTurnover.Core.Compare.DiffLine>();
            var added = new List<IbmTurnover.Core.Compare.DiffLine>();
            while (i < lines.Count && lines[i].Op == IbmTurnover.Core.Compare.LineOp.Removed) removed.Add(lines[i++]);
            while (i < lines.Count && lines[i].Op == IbmTurnover.Core.Compare.LineOp.Added) added.Add(lines[i++]);
            for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                var l = k < removed.Count ? removed[k] : null;
                var r = k < added.Count ? added[k] : null;
                rows.Add(new Row(l?.LeftNo, l?.Left, l is null ? "pad" : "del", r?.RightNo, r?.Right, r is null ? "pad" : "add"));
            }
        }
        return rows;
    }

    public static IHtmlContent Icon(string name, string cls = "ico") =>
        new HtmlString($"<svg class=\"{cls}\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"{Paths[name]}\"/></svg>");
}
