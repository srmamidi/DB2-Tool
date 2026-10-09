using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Workflow;
using Microsoft.AspNetCore.Http.Extensions;

namespace IbmTurnover.Web.Models;

/// <summary>Everything a comparison needs, bound from the query string (GET) or the package form (POST).</summary>
public sealed class CompareQuery
{
    public string? Mode { get; set; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public string[] Logicals { get; set; } = [];
    public string? LibEnv { get; set; }
    public string? FromLib { get; set; }
    public string? ToLib { get; set; }
    /// <summary>Optional master library for programs, DDS, commands, data areas/queues, job descriptions, messages.</summary>
    public string? PgmLib { get; set; }
    /// <summary>Optional master source library for copied source members.</summary>
    public string? SrcLib { get; set; }
    /// <summary>Developer / personal library: only objects it contains are compared.</summary>
    public bool SourceOnly { get; set; }
    /// <summary>Snapshot id for the source side ("live" or empty = live).</summary>
    public string? Ss { get; set; }
    public string? Ts { get; set; }
    public bool Refresh { get; set; }
    /// <summary>cards or report.</summary>
    public string? View { get; set; }

    public bool Run { get; private set; }
    public string? SourceSnapshot => FileSnapshotStore.IsValidId(Ss) ? Ss : null;
    public string? TargetSnapshot => FileSnapshotStore.IsValidId(Ts) ? Ts : null;
    public string SourceKey => Mode == "library" ? LibEnv! : Source!;
    public string TargetKey => Mode == "library" ? LibEnv! : Target!;

    public void Normalize(IReadOnlyList<TurnoverEnvironment> envs)
    {
        Mode = Mode == "library" ? "library" : "promote";
        Run = !string.IsNullOrWhiteSpace(Source);
        string Known(string? key, string fallback) => envs.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Key ?? fallback;
        Source = Known(Source, envs[0].Key);
        Target = Known(Target, envs.Count > 1 ? envs[1].Key : envs[0].Key);
        LibEnv = Known(LibEnv, Source);
        FromLib = (FromLib ?? "").Trim().ToUpperInvariant();
        ToLib = (ToLib ?? "").Trim().ToUpperInvariant();
        PgmLib = string.IsNullOrWhiteSpace(PgmLib) ? null : PgmLib.Trim().ToUpperInvariant();
        SrcLib = string.IsNullOrWhiteSpace(SrcLib) ? null : SrcLib.Trim().ToUpperInvariant();
        View = View == "report" ? "report" : "cards";
        Logicals = Logicals.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().ToUpperInvariant()).Distinct().ToArray();
    }

    /// <summary>This comparison as a URL (the view and refresh flags can be overridden).</summary>
    public string Url(string path, string? view = null, bool refresh = false, string? ss = null, string? ts = null)
    {
        var qb = new QueryBuilder { { "mode", Mode ?? "promote" } };
        if (Mode == "library")
        {
            qb.Add("libEnv", LibEnv ?? ""); qb.Add("fromLib", FromLib ?? ""); qb.Add("toLib", ToLib ?? "");
            if (PgmLib is not null) qb.Add("pgmLib", PgmLib);
            if (SrcLib is not null) qb.Add("srcLib", SrcLib);
            if (SourceOnly) qb.Add("sourceOnly", "true");
        }
        else
        {
            qb.Add("source", Source ?? ""); qb.Add("target", Target ?? "");
            foreach (var l in Logicals) qb.Add("logicals", l);
            if ((ss ?? SourceSnapshot) is { } s) qb.Add("ss", s);
            if ((ts ?? TargetSnapshot) is { } t) qb.Add("ts", t);
        }
        qb.Add("view", view ?? View ?? "cards");
        if (refresh) qb.Add("refresh", "true");
        return path + qb.ToQueryString();
    }
}

public sealed class CompareViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public IReadOnlyList<string> AllLogicals { get; init; } = [];
    public required CompareQuery Query { get; init; }
    public ComparisonResult? Result { get; init; }
    /// <summary>Large result: side-by-side panels are fetched when opened instead of rendered up front.</summary>
    public bool LazyDiffs { get; init; }
    public IReadOnlyList<SnapshotRecord> SourceHistory { get; init; } = [];
    public IReadOnlyList<SnapshotRecord> TargetHistory { get; init; } = [];
    /// <summary>Difference id → user holding the check-out of its source member.</summary>
    public IReadOnlyDictionary<string, string> Holders { get; init; } = new Dictionary<string, string>();
}

public sealed class CheckoutsViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public IReadOnlyList<Checkout> Items { get; init; } = [];
    public bool History { get; init; }
    public CheckoutSettings Settings { get; init; } = new();
}

public sealed class SourceViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public IReadOnlyList<(string Key, int Count, DateTimeOffset Last)> Keys { get; init; } = [];
    public string? Key { get; init; }
    public IReadOnlyList<SourceVersion> Versions { get; init; } = [];
    public SourceVersion? A { get; init; }
    public SourceVersion? B { get; init; }
    public string? TextA { get; init; }
    public string? TextB { get; init; }
}

public sealed class PackageViewModel
{
    public required TurnoverPackage Package { get; init; }
    public required ComparisonResult Comparison { get; init; }
    public required DeploymentPlan Plan { get; init; }
    public required string Script { get; init; }
    public required string Rollback { get; init; }
    public required TurnoverUser User { get; init; }
    public int ApprovalsRequired { get; init; }
    public string? AnalysisError { get; init; }
    public IReadOnlyList<SourceChange> SourceChanges { get; init; } = [];
    /// <summary>The target's deploy windows (server time); empty = any time.</summary>
    public IReadOnlyList<IbmTurnover.Core.Workflow.DeployWindow> Windows { get; init; } = [];
    public bool WindowOpen { get; init; } = true;
    public DateTime? NextOpen { get; init; }
    public DateTime? ScheduledLocal { get; init; }
    public IReadOnlyList<DistributionTarget> Distribution { get; init; } = [];
    public IReadOnlyList<Difference> Selected => Comparison.Differences.Where(d => Package.SelectedDiffIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList();
}

public sealed class LibraryMoveViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public string Env { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public bool KeepOriginal { get; init; } = true;
    public LibraryMoveResult? Result { get; init; }
}

public sealed class EnvironmentsViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public IReadOnlyList<string> Logicals { get; init; } = [];
    public IReadOnlyList<string> ProfileLogicals { get; init; } = [];
    public IReadOnlyList<string> TrackedProfiles { get; init; } = [];
    public bool IsLive { get; init; }
    public string? TestedEnv { get; init; }
    public IReadOnlyList<(string Check, bool Ok, string Message)> TestResults { get; init; } = [];
}

public sealed class DashboardViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public IReadOnlyList<TurnoverPackage> Packages { get; init; } = [];
    public bool IsLive { get; init; }
}

public sealed class SnapshotsViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public string Env { get; init; } = "";
    public IReadOnlyList<SnapshotRecord> History { get; init; } = [];
    public SnapshotSettings Settings { get; init; } = new();
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
}

public sealed class WorkflowViewModel
{
    public IReadOnlyList<TurnoverEnvironment> Environments { get; init; } = [];
    public string Env { get; init; } = "";
    public CatalogLoadReport? Report { get; init; }
    public bool IsLive { get; init; }
    public IReadOnlyList<TurnoverPackage> Packages { get; init; } = [];
    public required TurnoverOptions Options { get; init; }

    /// <summary>Checks grouped by what was read (across libraries): status, total time, rows.</summary>
    public IReadOnlyList<(string Name, int Ok, int Failed, long Ms, int Rows, string? Error)> CheckGroups =>
        (Report?.Checks ?? []).GroupBy(c => c.Name)
            .Select(g => (g.Key, g.Count(c => c.Ok), g.Count(c => !c.Ok), g.Sum(c => c.Milliseconds), g.Sum(c => c.Rows), g.FirstOrDefault(c => !c.Ok)?.Error))
            .ToList();
}

public sealed record DriftSummary(string Source, string Target, int Changes, int Blockers, int Warnings, IReadOnlyDictionary<string, int> ByKind, string? Error);

/// <summary>One difference card. LazyUrl set: the side-by-side panel is fetched when the card opens.</summary>
public sealed record DiffCard(Difference D, bool Selectable, bool Selected, string SourceKey, string TargetKey, string? LazyUrl = null, string? CheckedOutBy = null);
