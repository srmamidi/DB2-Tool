using System.Globalization;
using System.Text.RegularExpressions;

namespace IbmTurnover.Core.Workflow;

/// <summary>
/// A weekly deployment window such as "Sat 22:00-02:00" (crosses midnight) or "Daily 23:00-23:59". Several windows are
/// separated by ';'. Times are the web server's local time.
/// </summary>
public sealed record DeployWindow(DayOfWeek? Day, TimeSpan Start, TimeSpan End)
{
    private static readonly Regex Rx = new(@"^(?<day>mon|tue|wed|thu|fri|sat|sun|daily|\*)\s+(?<s>\d{1,2}:\d{2})\s*-\s*(?<e>\d{1,2}:\d{2})$", RegexOptions.IgnoreCase);

    public static IReadOnlyList<DeployWindow> Parse(string? text, string env)
    {
        var list = new List<DeployWindow>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Rx.Match(part);
            if (!m.Success || !TimeSpan.TryParseExact(m.Groups["s"].Value, @"h\:mm", CultureInfo.InvariantCulture, out var s)
                           || !TimeSpan.TryParseExact(m.Groups["e"].Value, @"h\:mm", CultureInfo.InvariantCulture, out var e)
                           || s >= TimeSpan.FromDays(1) || e >= TimeSpan.FromDays(1) || s == e)
                throw new TurnoverException($"Environment '{env}' DeployWindows: '{part}' is not like 'Sat 22:00-02:00' or 'Daily 23:00-23:59'.");
            var d = m.Groups["day"].Value.ToLowerInvariant();
            DayOfWeek? day = d is "daily" or "*" ? null : d switch
            {
                "mon" => DayOfWeek.Monday, "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday, "thu" => DayOfWeek.Thursday,
                "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday,
            };
            list.Add(new DeployWindow(day, s, e));
        }
        return list;
    }

    /// <summary>True when <paramref name="local"/> falls inside this window (a window that crosses midnight belongs to the day it starts).</summary>
    public bool Contains(DateTime local)
    {
        var t = local.TimeOfDay;
        if (Start < End) return (Day is null || local.DayOfWeek == Day) && t >= Start && t < End;
        // Crosses midnight: the evening part on Day, the morning part on the next day.
        if (t >= Start) return Day is null || local.DayOfWeek == Day;
        if (t < End) return Day is null || local.AddDays(-1).DayOfWeek == Day;
        return false;
    }

    public static bool IsOpen(IReadOnlyList<DeployWindow> windows, DateTime local) => windows.Count == 0 || windows.Any(w => w.Contains(local));

    /// <summary>The next moment (minute resolution, within 8 days) a window opens, for messages and scheduling.</summary>
    public static DateTime? NextOpen(IReadOnlyList<DeployWindow> windows, DateTime local)
    {
        if (windows.Count == 0) return local;
        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Kind);
        for (var i = 0; i < 8 * 24 * 60; i++, t = t.AddMinutes(1))
            if (windows.Any(w => w.Contains(t))) return t;
        return null;
    }

    public override string ToString() =>
        $"{(Day is null ? "Daily" : CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(Day.Value))} {Start:hh\\:mm}-{End:hh\\:mm}";
}

/// <summary>A CL command run around every deployment in an environment, with an optional undo ("A => B").</summary>
public sealed record DeployCommand(string Command, string? Undo)
{
    public static IReadOnlyList<DeployCommand> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p =>
            {
                var i = p.IndexOf("=>", StringComparison.Ordinal);
                return i < 0 ? new DeployCommand(p, null) : new DeployCommand(p[..i].Trim(), p[(i + 2)..].Trim() is { Length: > 0 } u ? u : null);
            })
            .ToList();
    }
}
