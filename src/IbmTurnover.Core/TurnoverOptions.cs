using IbmTurnover.Core.Models;

namespace IbmTurnover.Core;

public sealed class TurnoverOptions
{
    public const string Section = "Turnover";

    /// <summary>Sample = built-in demo catalog, nothing touches IBM i. Live = read catalogs and deploy over ODBC.</summary>
    public string Mode { get; set; } = "Sample";
    /// <summary>Lets the UI switch between demo users. Refused at startup in Production.</summary>
    public bool DemoUsers { get; set; }
    public string DataDirectory { get; set; } = "App_Data";
    public int ApprovalsRequired { get; set; } = 1;
    public int ProdApprovalsRequired { get; set; } = 2;
    /// <summary>Allows a justified override of blockers below Prod. Prod never allows it.</summary>
    public bool AllowBlockerOverrideBelowProd { get; set; } = true;
    public List<TurnoverEnvironment> Environments { get; set; } = new();
    public List<SequenceBinding> SequenceBindings { get; set; } = new();
    /// <summary>
    /// User profiles compared by the SAME name in every environment (group profiles, shared service accounts).
    /// Wildcards allowed: "ORD*". Environment-specific names go in each environment's Profiles map instead.
    /// </summary>
    public List<string> TrackedProfiles { get; set; } = new();
    /// <summary>"ORD*;APP*" form of <see cref="TrackedProfiles"/> for web.config.</summary>
    public string? TrackedProfilesText { get; set; }
    public CatalogSettings Catalog { get; set; } = new();
    public SnapshotSettings Snapshots { get; set; } = new();
    public IbmTurnover.Core.Workflow.CheckoutSettings Checkout { get; set; } = new();
    /// <summary>System values compared between environments (report; promoted only when ticked).</summary>
    public List<string> TrackedSystemValues { get; set; } = new();
    public string? TrackedSystemValuesText { get; set; }
    /// <summary>Authorization lists compared between environments.</summary>
    public List<string> TrackedAuthorizationLists { get; set; } = new();
    public string? TrackedAuthorizationListsText { get; set; }
    /// <summary>Job scheduler entries compared between environments, by name or prefix* (ORD*).</summary>
    public List<string> TrackedJobSchedules { get; set; } = new();
    public string? TrackedJobSchedulesText { get; set; }
    /// <summary>IFS stream files larger than this are compared by size and date only, not content.</summary>
    public long MaxIfsCompareBytes { get; set; } = 5_000_000;
    /// <summary>How long a deploy or rollback waits for another one to finish starting before it is refused.</summary>
    public int DeployGateWaitSeconds { get; set; } = 30;
    public SchedulerSettings Scheduler { get; set; } = new();

    public bool IsLive => Mode.Equals("Live", StringComparison.OrdinalIgnoreCase);

    public TurnoverEnvironment Env(string key) =>
        Environments.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new TurnoverException($"Unknown environment '{key}'.");

    /// <summary>
    /// Fails closed: a bad map would silently deploy into the wrong library, so every problem throws at startup.
    /// </summary>
    public void Validate(Func<string, string?>? connectionStringLookup = null)
    {
        if (!Mode.Equals("Sample", StringComparison.OrdinalIgnoreCase) && !IsLive)
            throw new TurnoverException($"Turnover:Mode must be 'Sample' or 'Live' (got '{Mode}').");
        if (Environments.Count < 2)
            throw new TurnoverException("Turnover:Environments needs at least two environments.");
        if (ApprovalsRequired < 1 || ProdApprovalsRequired < 1)
            throw new TurnoverException("Approval counts must be at least 1.");

        if (!string.IsNullOrWhiteSpace(TrackedProfilesText))
            TrackedProfiles.AddRange(TrackedProfilesText.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        TrackedProfiles = TrackedProfiles.Select(x => x.ToUpperInvariant()).Distinct().ToList();
        foreach (var t in TrackedProfiles)
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Z#@$][A-Z0-9_#@$]{0,9}\*?$|^[A-Z#@$]?\*$"))
                throw new TurnoverException($"TrackedProfiles: '{t}' is not a profile name or prefix* pattern.");
        if (Catalog.CacheMinutes < 0 || Catalog.MaxParallelLibraries < 1 || Catalog.CommandTimeoutSeconds < 1)
            throw new TurnoverException("Turnover:Catalog: CacheMinutes >= 0, MaxParallelLibraries >= 1, CommandTimeoutSeconds >= 1.");
        if (DeployGateWaitSeconds is < 1 or > 600 || Scheduler.IntervalSeconds is < 10 or > 3600)
            throw new TurnoverException("Turnover: DeployGateWaitSeconds must be 1-600 and Scheduler:IntervalSeconds 10-3600.");
        if (Snapshots.RetainPerEnvironment < 1 || Snapshots.AutoCaptureMinutes < 0)
            throw new TurnoverException("Turnover:Snapshots: RetainPerEnvironment >= 1 and AutoCaptureMinutes >= 0.");

        static List<string> Names(List<string> list, string? text, string what)
        {
            if (!string.IsNullOrWhiteSpace(text)) list.AddRange(text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var result = list.Select(x => x.ToUpperInvariant()).Distinct().ToList();
            foreach (var n in result)
                if (!System.Text.RegularExpressions.Regex.IsMatch(n, @"^[A-Z#@$][A-Z0-9_#@$]{0,9}\*?$"))
                    throw new TurnoverException($"{what}: '{n}' is not an IBM i name (or prefix*).");
            return result;
        }
        TrackedSystemValues = Names(TrackedSystemValues, TrackedSystemValuesText, "TrackedSystemValues");
        TrackedAuthorizationLists = Names(TrackedAuthorizationLists, TrackedAuthorizationListsText, "TrackedAuthorizationLists");
        TrackedJobSchedules = Names(TrackedJobSchedules, TrackedJobSchedulesText, "TrackedJobSchedules");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var env in Environments)
        {
            if (env.IfsPaths.Count == 0 && !string.IsNullOrWhiteSpace(env.IfsPathsText))
                foreach (var part in env.IfsPathsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) throw new TurnoverException($"Environment '{env.Key}' IfsPathsText: '{part}' is not LOGICAL=/path.");
                    env.IfsPaths[part[..eq].Trim().ToUpperInvariant()] = part[(eq + 1)..].Trim();
                }
            // Case-insensitive keys (the config binder may hand over an ordinal dictionary); no trailing slash on roots.
            env.IfsPaths = env.IfsPaths.ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => kv.Value.Trim().TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
            foreach (var path in env.IfsPaths.Values)
                if (!IsSafeIfsPath(path)) throw new TurnoverException($"Environment '{env.Key}': IFS path '{path}' must be absolute and must not contain quotes, '..' or control characters.");
            if (env.Distribution.Count == 0 && !string.IsNullOrWhiteSpace(env.DistributionText))
                foreach (var part in env.DistributionText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var bits = part.Split(':', StringSplitOptions.TrimEntries);
                    env.Distribution.Add(new DistributionTarget
                    {
                        Environment = bits[0].ToUpperInvariant(),
                        Mode = bits.Length > 1 && Enum.TryParse<DistributionMode>(bits[1], true, out var m) ? m
                             : bits.Length > 1 ? throw new TurnoverException($"Environment '{env.Key}' DistributionText: mode '{bits[1]}' must be Deploy or Verify.") : DistributionMode.Deploy,
                    });
                }
            IbmTurnover.Core.Workflow.DeployWindow.Parse(env.DeployWindows, env.Key);   // throws on a malformed window
            foreach (var cmd in IbmTurnover.Core.Workflow.DeployCommand.Parse(env.PreDeployCommands).Concat(IbmTurnover.Core.Workflow.DeployCommand.Parse(env.PostDeployCommands)))
                if (cmd.Command.Contains('\'') && cmd.Command.Count(ch => ch == '\'') % 2 != 0)
                    throw new TurnoverException($"Environment '{env.Key}': unbalanced quotes in deploy command '{cmd.Command}'.");
            if (env.Libraries.Count == 0) env.Libraries = MapText.Parse(env.LibrariesText, $"Environment '{env.Key}' LibrariesText");
            if (env.Profiles.Count == 0) env.Profiles = MapText.Parse(env.ProfilesText, $"Environment '{env.Key}' ProfilesText");
            env.Profiles = env.Profiles.ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => kv.Value.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
            foreach (var prof in env.Profiles.Values)
                if (!IsValidLibraryName(prof))
                    throw new TurnoverException($"Environment '{env.Key}': '{prof}' is not a valid user profile name.");
            if (string.IsNullOrWhiteSpace(env.Key)) throw new TurnoverException("Every environment needs a Key.");
            // Host decides "same partition" (CPYF / CPY) versus a two-connection copy: blank would mean "same" for every pair.
            if (string.IsNullOrWhiteSpace(env.Host)) throw new TurnoverException($"Environment '{env.Key}' needs a Host (the IBM i system name).");
            if (!keys.Add(env.Key)) throw new TurnoverException($"Duplicate environment key '{env.Key}'.");
            if (env.Libraries.Count == 0) throw new TurnoverException($"Environment '{env.Key}' maps no libraries.");
            // Rebuild with a case-insensitive comparer: the config binder may hand us an ordinal dictionary.
            env.Libraries = env.Libraries.ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => kv.Value.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
            foreach (var lib in env.Libraries.Values)
                if (!IsValidLibraryName(lib))
                    throw new TurnoverException($"Environment '{env.Key}': '{lib}' is not a valid IBM i library name (1-10 chars, A-Z 0-9 _ # @ $, not starting with a digit).");
            if (IsLive)
            {
                if (string.IsNullOrWhiteSpace(env.ConnectionStringName))
                    throw new TurnoverException($"Live mode: environment '{env.Key}' has no ConnectionStringName.");
                var cs = connectionStringLookup?.Invoke(env.ConnectionStringName);
                if (connectionStringLookup is not null && string.IsNullOrWhiteSpace(cs))
                    throw new TurnoverException($"Live mode: connection string '{env.ConnectionStringName}' for '{env.Key}' is missing or empty.");
                // NAM / CMT contradictions and malformed strings stop the app now, not at the first connection.
                if (!string.IsNullOrWhiteSpace(cs))
                {
                    try { IbmTurnover.Core.Catalog.OdbcConnectionFactory.Normalize(cs, env.Key); }
                    catch (ArgumentException ex) { throw new TurnoverException($"Live mode: connection string '{env.ConnectionStringName}' for '{env.Key}' is malformed ({ex.Message})."); }
                }
            }
        }

        // Binding names end up inside generated SQL, so they must be plain identifiers.
        foreach (var b in SequenceBindings)
            if (!new[] { b.Sequence, b.Table, b.Column }.All(n => System.Text.RegularExpressions.Regex.IsMatch(n ?? "", "^[A-Z_#@$][A-Z0-9_#@$]{0,127}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new TurnoverException($"SequenceBindings: '{b.Sequence}' → '{b.Table}.{b.Column}' contains an invalid identifier.");

        // Distribution targets must exist, must not point at themselves, and Deploy targets must map every library the
        // same way (the approved script is run unchanged there).
        foreach (var env in Environments)
            foreach (var t in env.Distribution)
            {
                var other = Environments.FirstOrDefault(e => e.Key.Equals(t.Environment, StringComparison.OrdinalIgnoreCase))
                            ?? throw new TurnoverException($"Environment '{env.Key}' distributes to unknown environment '{t.Environment}'.");
                if (other == env) throw new TurnoverException($"Environment '{env.Key}' cannot distribute to itself.");
                if (t.Mode == DistributionMode.Deploy && (other.Libraries.Count != env.Libraries.Count
                        || env.Libraries.Any(kv => !string.Equals(other.PhysicalFor(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase))))
                    throw new TurnoverException($"Distribution {env.Key} → {other.Key}: Deploy targets must map every logical library to the same physical library.");
                // The replica runs the primary's approved script, including its pre/post commands.
                if (t.Mode == DistributionMode.Deploy && (!string.Equals(other.PreDeployCommands ?? "", env.PreDeployCommands ?? "", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(other.PostDeployCommands ?? "", env.PostDeployCommands ?? "", StringComparison.OrdinalIgnoreCase)))
                    throw new TurnoverException($"Distribution {env.Key} → {other.Key}: Deploy targets run the same script, so PreDeployCommands and PostDeployCommands must match.");
            }

        // Two environments on the same partition must never share a physical library:
        // promoting "Dev → QA" would then overwrite the library you are promoting from.
        foreach (var host in Environments.GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase))
        {
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var env in host)
                foreach (var lib in env.Libraries.Values)
                    if (owner.TryGetValue(lib, out var other) && !other.Equals(env.Key, StringComparison.OrdinalIgnoreCase))
                        throw new TurnoverException($"Library {lib} is mapped by both '{other}' and '{env.Key}' on host '{host.Key}'.");
                    else owner[lib] = env.Key;
        }
    }

    public static bool IsSafeIfsPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') && !path.Contains("..") && !path.Contains('\'') && !path.Contains('"')
        && !path.Any(char.IsControl) && path.Length <= 1024;

    public static bool IsValidLibraryName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 10 &&
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z#@$][A-Z0-9_#@$]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

public sealed class CatalogSettings
{
    /// <summary>How long a catalog read is reused before IBM i is asked again (0 = always read). Sample mode ignores it.</summary>
    public int CacheMinutes { get; set; } = 10;
    /// <summary>Libraries read in parallel, each on its own connection.</summary>
    public int MaxParallelLibraries { get; set; } = 4;
    public int CommandTimeoutSeconds { get; set; } = 300;
    /// <summary>Hash every source member so "changed" means the text changed. Costs one query per program.</summary>
    public bool HashSourceMembers { get; set; } = true;
}

public sealed class SnapshotSettings
{
    public int RetainPerEnvironment { get; set; } = 60;
    /// <summary>Store every fresh catalog read as a snapshot (unchanged structure is recorded without a new copy).</summary>
    public bool CaptureOnCompare { get; set; } = true;
    /// <summary>Background capture of every environment every N minutes (0 = off).</summary>
    public int AutoCaptureMinutes { get; set; }
}

public sealed class SchedulerSettings
{
    /// <summary>How often the background scheduler looks for packages whose deploy time has come.</summary>
    public int IntervalSeconds { get; set; } = 60;
}

public class TurnoverException(string message) : Exception(message);
