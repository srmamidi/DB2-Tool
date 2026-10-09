using System.Text.Json.Serialization;

namespace IbmTurnover.Core.Models;

public enum ObjectKind
{
    Library, Table, Column, CheckConstraint, Index, ForeignKey, Sequence, Alias, View, Function, Procedure, Trigger,
    DdsFile, Program, ServiceProgram, Command, DataArea, DataQueue, Message, JobDescription, UserProfile, Authority,
    /// <summary>*MENU and *PNLGRP (UIM help / menus).</summary>
    Menu, BindingDirectory, AuthorizationList, JobSchedule, SystemValue, IfsFile,
}

/// <summary>One attribute that differs, with both values as text: the row of the change report.</summary>
public sealed record AttributeChange(string Attribute, string? Before, string? After);

/// <summary>Relative to the TARGET: Added = exists in source, not in target.</summary>
public enum ChangeKind { Added, Removed, Modified, Renamed, Drift }

public enum Severity { Info = 0, Warning = 1, Blocker = 2 }

/// <summary>A rule hit. Code is stable (e.g. FK003) so teams can document and search them.</summary>
public sealed class Finding
{
    public Severity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Remedy { get; set; }
    /// <summary>The generated script already fixes this; it is shown, but does not block.</summary>
    public bool AutoFixed { get; set; }

    public static Finding Of(Severity s, string code, string message, string? remedy = null, bool autoFixed = false) =>
        new() { Severity = s, Code = code, Message = message, Remedy = remedy, AutoFixed = autoFixed };
}

/// <summary>A pre-flight query that must return 0 for the deployment to proceed.</summary>
public sealed record PreCheck(string Title, string Sql);

public sealed class Difference
{
    public string Id { get; set; } = "";
    public ObjectKind Kind { get; set; }
    public ChangeKind Change { get; set; }
    public string Logical { get; set; } = "";
    public string SourceLibrary { get; set; } = "";
    public string TargetLibrary { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Parent { get; set; }
    public List<string> Details { get; set; } = new();
    /// <summary>Attribute-level before/after values (target now → after promotion) for the report view and CSV.</summary>
    public List<AttributeChange> Changes { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public List<PreCheck> PreChecks { get; set; } = new();
    /// <summary>Ids of other differences this one needs (e.g. an FK needs its parent table).</summary>
    public List<string> DependsOn { get; set; } = new();
    /// <summary>CL commands to run after this change ships (e.g. UPDPGM to re-bind callers of a service program).</summary>
    public List<string> FollowUps { get; set; } = new();
    /// <summary>For column diffs: the 10-char system name of the table (used for backups).</summary>
    public string? TableSystemName { get; set; }
    /// <summary>When a sequence/identity is behind the data: the value the script restarts it at.</summary>
    public long? RestartWith { get; set; }
    public string? SourceDdl { get; set; }
    public string? TargetDdl { get; set; }
    /// <summary>Default selection on the compare screen. Destructive or no-op items start unticked.</summary>
    public bool DefaultSelected { get; set; } = true;
    public bool Destructive { get; set; }

    [JsonIgnore] public object? Source { get; set; }
    [JsonIgnore] public object? Target { get; set; }

    public Severity Severity => Findings.Count == 0 ? Severity.Info : Findings.Max(f => f.Severity);
    public bool HasOpenBlocker => Findings.Any(f => f.Severity == Severity.Blocker && !f.AutoFixed);
    public string DisplayName => Parent is null ? Name : $"{Parent}.{Name}";

    public static string MakeId(ObjectKind kind, string logical, string name, string? parent = null) =>
        $"{kind}:{logical}:{(parent is null ? "" : parent + ".")}{name}".ToUpperInvariant();
}

public sealed class ComparisonResult
{
    public TurnoverEnvironment Source { get; set; } = new();
    public TurnoverEnvironment Target { get; set; } = new();
    public List<LibraryPair> Pairs { get; set; } = new();
    public List<Difference> Differences { get; set; } = new();
    public List<Finding> GlobalFindings { get; set; } = new();
    /// <summary>Hash over all target libraries compared. Changes if someone alters the target.</summary>
    public string TargetFingerprint { get; set; } = "";
    public DateTimeOffset ComparedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>"Live" or the snapshot id each side was read from.</summary>
    public string SourceLabel { get; set; } = "Live";
    public string TargetLabel { get; set; } = "Live";
    public bool SourceOnly { get; set; }
    /// <summary>Library mode: master source library that copied members go to (otherwise mapped from the source library).</summary>
    public string? SourceFileLibrary { get; set; }
    /// <summary>Target libraries that exist right now (read from the catalog).</summary>
    public List<string> TargetLibrariesPresent { get; set; } = new();
    public bool IsLiveOnBothSides => SourceLabel == "Live" && TargetLabel == "Live";

    public int Count(Severity s) => Differences.Count(d => d.Severity == s) + GlobalFindings.Count(f => f.Severity == s);
    public Difference? Find(string id) => Differences.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The safe default selection: everything ticked by default with no open blocker, minus anything whose
    /// dependency is not in the set (repeated until stable), plus level-check recompiles only when a
    /// table change that causes them is in the set.
    /// </summary>
    public HashSet<string> RecommendedSelection()
    {
        static bool LevelCheck(Difference d) => d.Kind is ObjectKind.Program or ObjectKind.ServiceProgram && d.Change == ChangeKind.Drift;
        var set = new HashSet<string>(Differences.Where(d => d.DefaultSelected && !d.HasOpenBlocker && !LevelCheck(d)).Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var d in Differences.Where(d => set.Contains(d.Id)).ToList())
                if (d.DependsOn.Any(dep => Find(dep) is not null && !set.Contains(dep))) { set.Remove(d.Id); changed = true; }
        } while (changed);
        foreach (var d in Differences.Where(LevelCheck))
            if (d.DependsOn.Any(set.Contains)) set.Add(d.Id);
        return set;
    }
}
