using IbmTurnover.Core;

namespace IbmTurnover.Core.Models;

/// <summary>Promotion order. Changes move up one tier at a time (Dev → QA → Stage → Prod).</summary>
public enum EnvironmentTier { Dev = 0, QA = 1, Stage = 2, Prod = 3 }

/// <summary>
/// One environment (an IBM i partition, or a set of libraries on a shared partition).
/// <see cref="Libraries"/> maps a LOGICAL schema name (e.g. ORDERS) to the PHYSICAL library in this
/// environment (e.g. ORDDEV). The same logical name in two environments is how the tool knows
/// that ORDDEV in Dev is "the same thing" as ORDQA in QA.
/// </summary>
public sealed class TurnoverEnvironment
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public EnvironmentTier Tier { get; set; }
    public string Host { get; set; } = "";
    /// <summary>Name of the connection string (ConnectionStrings section). Required in Live mode.</summary>
    public string? ConnectionStringName { get; set; }
    public Dictionary<string, string> Libraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Same map as one string, for web.config appSettings: "CUSTOMER=CUSDEV;ORDERS=ORDDEV".</summary>
    public string? LibrariesText { get; set; }
    /// <summary>Logical profile → physical user profile here, e.g. ORDER_SVC → SVCORDDEV (service accounts differ per environment).</summary>
    public Dictionary<string, string> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Same map as one string: "ORDER_SVC=SVCORDDEV;ORDER_GRP=ORDUSERS".</summary>
    public string? ProfilesText { get; set; }

    public string? PhysicalProfile(string logical) => Profiles.TryGetValue(logical, out var p) ? p : null;

    /// <summary>Logical IFS location → directory here, e.g. WEB=/www/orders/dev. Stream files under it are compared and promoted.</summary>
    public Dictionary<string, string> IfsPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"WEB=/www/orders/dev;CFG=/orders/config/dev".</summary>
    public string? IfsPathsText { get; set; }

    /// <summary>
    /// Other systems that receive this environment's deployments (multi-partition production, DR). Deploy = run the same
    /// approved script there too; Verify = an HA tool replicates, so only check the objects arrived.
    /// </summary>
    public List<DistributionTarget> Distribution { get; set; } = new();
    /// <summary>"PRODDR:Deploy;PRODHA:Verify".</summary>
    public string? DistributionText { get; set; }

    /// <summary>When deployments may run, e.g. "Sat 22:00-02:00;Wed 23:00-23:59" (server local time). Empty = any time.</summary>
    public string? DeployWindows { get; set; }

    /// <summary>CL run before every deployment here, each with its undo: "HLDJOBQ JOBQ(QGPL/ORDJOBQ) => RLSJOBQ JOBQ(QGPL/ORDJOBQ)", separated by '|'.</summary>
    public string? PreDeployCommands { get; set; }
    /// <summary>CL run after every deployment here — also after a failure (e.g. RLSJOBQ), separated by '|'.</summary>
    public string? PostDeployCommands { get; set; }

    public string? PhysicalFor(string logical) =>
        Libraries.TryGetValue(logical, out var lib) ? lib : null;

    public string? LogicalFor(string physical) =>
        Libraries.FirstOrDefault(kv => kv.Value.Equals(physical, StringComparison.OrdinalIgnoreCase)).Key;
}

public sealed record LibraryPair(string Logical, string SourceLibrary, string TargetLibrary);

/// <summary>Ties a sequence to the key column it feeds, so the tool can check the sequence is ahead of the data.</summary>
public sealed class SequenceBinding
{
    public string Logical { get; set; } = "";
    public string Sequence { get; set; } = "";
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
}

public enum DistributionMode { Deploy, Verify }

public sealed class DistributionTarget
{
    public string Environment { get; set; } = "";
    public DistributionMode Mode { get; set; } = DistributionMode.Deploy;
}

public static class MapText
{
    /// <summary>Parses "A=B;C=D" (also accepts commas and new lines). Throws on a malformed pair: a half-read map is worse than none.</summary>
    public static Dictionary<string, string> Parse(string? text, string what)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;
        foreach (var part in text.Split([';', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) throw new TurnoverException($"{what}: '{part}' is not LOGICAL=PHYSICAL.");
            var key = part[..eq].Trim().ToUpperInvariant();
            if (!map.TryAdd(key, part[(eq + 1)..].Trim().ToUpperInvariant())) throw new TurnoverException($"{what}: '{key}' appears twice.");
        }
        return map;
    }
}
