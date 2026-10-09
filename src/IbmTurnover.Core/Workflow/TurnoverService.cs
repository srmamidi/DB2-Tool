using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using Microsoft.Extensions.Options;

namespace IbmTurnover.Core.Workflow;

/// <summary>
/// The turnover workflow: compare → package → submit → approve → dry run → deploy → (rollback).
/// Every rule is enforced here, not in the UI, so an integrating app cannot skip one by posting a form.
/// </summary>
public sealed class TurnoverService(
    IOptions<TurnoverOptions> options,
    CatalogService catalog,
    ITurnoverRepository repo,
    IDeploymentExecutor executor,
    SimulatedExecutor simulator,
    ISnapshotStore snapshots,
    CheckoutService checkouts,
    SourceArchiveService sources,
    TimeProvider clock)
{
    private readonly TurnoverOptions _o = options.Value;
    private readonly SchemaComparer _comparer = new();
    private readonly DeploymentPlanner _planner = new();

    public TurnoverOptions Options => _o;
    public IReadOnlyList<TurnoverPackage> Packages() => repo.List();
    public TurnoverPackage Package(int number) => repo.Get(number) ?? throw new TurnoverException($"Package {number} not found.");

    // ------------------------------------------------------------------ compare

    /// <summary>
    /// Environment to environment. Each side is read live (cached for Catalog:CacheMinutes) unless a snapshot id is
    /// given; the same environment on both sides with a snapshot is a history comparison ("what changed in QA since Monday").
    /// </summary>
    public async Task<ComparisonResult> CompareAsync(string sourceKey, string targetKey, IEnumerable<string>? logicals, CancellationToken ct = default,
        string? sourceSnapshot = null, string? targetSnapshot = null, bool refresh = false)
    {
        var src = _o.Env(sourceKey);
        var tgt = _o.Env(targetKey);
        var srcSnap = await SideAsync(src, sourceSnapshot, refresh, ct);
        var tgtSnap = ReferenceEquals(src, tgt) && sourceSnapshot == targetSnapshot ? srcSnap : await SideAsync(tgt, targetSnapshot, refresh, ct);

        if (ReferenceEquals(src, tgt))
        {
            var result = CompareHistory(_o, src, srcSnap, tgtSnap);
            result.SourceLabel = sourceSnapshot ?? "Live";
            result.TargetLabel = targetSnapshot ?? "Live";
            return result;
        }

        var wanted = (logicals ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.ToUpperInvariant()).Distinct().ToList();
        if (wanted.Count == 0) wanted = src.Libraries.Keys.Where(tgt.Libraries.ContainsKey).ToList();
        var pairs = LibraryMap.PairsFor(src, tgt, wanted);
        var cmp = _comparer.Compare(new CompareInput
        {
            Source = src, Target = tgt, AllEnvironments = _o.Environments, Pairs = pairs,
            SourceSnapshots = srcSnap.Libraries, TargetSnapshots = tgtSnap.Libraries,
            Map = LibraryMap.ForPromotion(src, tgt, _o.Environments),
            SourceProfiles = srcSnap.Profiles, TargetProfiles = tgtSnap.Profiles,
            Profiles = new ProfileMap(src, tgt, _o.Environments, _o.TrackedProfiles),
            SourceSystem = srcSnap.System, TargetSystem = tgtSnap.System,
        });
        cmp.SourceLabel = sourceSnapshot ?? "Live";
        cmp.TargetLabel = targetSnapshot ?? "Live";
        foreach (var l in wanted.Where(l => !pairs.Any(p => p.Logical == l)))
            cmp.GlobalFindings.Add(Finding.Of(Severity.Blocker, "LIB004",
                $"Logical library {l} is not mapped in both {src.Key} and {tgt.Key}.", "Add it to Turnover:Environments for both environments."));
        return cmp;
    }

    /// <summary>Two points in time of ONE environment: every library and tracked profile, mapped to itself.</summary>
    public static ComparisonResult CompareHistory(TurnoverOptions o, TurnoverEnvironment env, EnvironmentSnapshot before, EnvironmentSnapshot after)
    {
        var libs = before.Libraries.Keys.Union(after.Libraries.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var pairs = libs.Select(l => new LibraryPair(env.LogicalFor(l) ?? l, l, l)).ToList();
        return new SchemaComparer().Compare(new CompareInput
        {
            Source = env, Target = env, AllEnvironments = o.Environments, Pairs = pairs,
            SourceSnapshots = after.Libraries, TargetSnapshots = before.Libraries,
            Map = new LibraryMap(pairs, o.Environments, env), Mode = CompareMode.History,
            SourceProfiles = after.Profiles, TargetProfiles = before.Profiles, Profiles = ProfileMap.Identity(env),
            SourceSystem = after.System, TargetSystem = before.System,
        });
    }

    /// <summary>
    /// Library to library on the same IBM i — including a developer's personal library into the master library.
    /// <paramref name="sourceOnly"/>: the source holds only the changed objects, so target-only objects are not listed.
    /// </summary>
    public async Task<ComparisonResult> CompareLibrariesAsync(string envKey, string fromLibrary, string toLibrary, CancellationToken ct = default,
        bool sourceOnly = false, bool refresh = false, string? programLibrary = null, string? sourceFileLibrary = null)
    {
        programLibrary = string.IsNullOrWhiteSpace(programLibrary) ? null : programLibrary.Trim().ToUpperInvariant();
        sourceFileLibrary = string.IsNullOrWhiteSpace(sourceFileLibrary) ? null : sourceFileLibrary.Trim().ToUpperInvariant();
        foreach (var l in new[] { programLibrary, sourceFileLibrary })
            if (l is not null && !TurnoverOptions.IsValidLibraryName(l)) throw new TurnoverException($"'{l}' is not a valid library name.");
        var env = _o.Env(envKey);
        string from = fromLibrary.Trim().ToUpperInvariant(), to = toLibrary.Trim().ToUpperInvariant();
        if (!TurnoverOptions.IsValidLibraryName(from) || !TurnoverOptions.IsValidLibraryName(to))
            throw new TurnoverException("Library names must be 1-10 characters: A-Z, 0-9, _ # @ $, not starting with a digit.");
        if (from == to) throw new TurnoverException("From and To are the same library.");
        var pair = new LibraryPair(env.LogicalFor(to) ?? env.LogicalFor(from) ?? to, from, to);
        // Every other library of the environment maps to itself, so references to them are "mapped", not unknown.
        var identity = env.Libraries.Values.Where(l => l != from && l != to).Select(l => new LibraryPair(l, l, l));
        var map = new LibraryMap(identity.Append(pair), _o.Environments, env);

        var envSnap = await catalog.LoadAsync(env, ct, refresh);
        // Copy: the cached environment snapshot is shared and must never gain a personal library.
        var libs = new Dictionary<string, SchemaSnapshot>(envSnap.Libraries, StringComparer.OrdinalIgnoreCase);
        var trace = new CatalogTrace();
        foreach (var lib in new[] { from, to, programLibrary }.OfType<string>().Where(l => !libs.ContainsKey(l)))
            if (await catalog.Provider.GetSnapshotAsync(env, lib, trace, ct) is { } extra) libs[lib] = extra;
        if (!libs.ContainsKey(from)) throw new TurnoverException($"Library {from} was not found in {env.Key}.");

        // The source library is never a deployment target: its programs must not get level-check recompiles.
        var targets = libs.Where(kv => !kv.Key.Equals(from, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var result = _comparer.Compare(new CompareInput
        {
            Source = env, Target = env, AllEnvironments = _o.Environments, Pairs = [pair],
            SourceSnapshots = libs, TargetSnapshots = targets, Map = map, Mode = CompareMode.Library, SourceOnly = sourceOnly,
            ProgramLibrary = programLibrary, SourceFileLibrary = sourceFileLibrary,
        });
        return result;
    }

    private async Task<EnvironmentSnapshot> SideAsync(TurnoverEnvironment env, string? snapshotId, bool refresh, CancellationToken ct) =>
        snapshotId is null ? await catalog.LoadAsync(env, ct, refresh) : snapshots.Load(env.Key, snapshotId);

    private Task<ComparisonResult> CompareForAsync(TurnoverPackage p, CancellationToken ct, bool refresh) =>
        p.ExplicitPairs.Count > 0
            ? CompareLibrariesAsync(p.SourceEnv, p.ExplicitPairs[0].SourceLibrary, p.ExplicitPairs[0].TargetLibrary, ct, p.SourceOnly, refresh,
                programLibrary: p.ProgramLibrary, sourceFileLibrary: p.SourceFileLibrary)
            : CompareAsync(p.SourceEnv, p.TargetEnv, p.Logicals, ct, refresh: refresh);

    /// <param name="fresh">Read IBM i now instead of using the catalog cache. Approval and deployment always do:
    /// "the target has not changed since approval" must not be judged on a read that is minutes old.</param>
    public async Task<(ComparisonResult Comparison, DeploymentPlan Plan)> AnalyzeAsync(TurnoverPackage p, CancellationToken ct = default, bool fresh = false)
    {
        var cmp = await CompareForAsync(p, ct, fresh);
        var plan = _planner.Build(cmp, p.SelectedDiffIds, p.Code, p.BackupLibrary, p.CustomSteps);
        // Process rules on top of the technical plan (they do not change the script, so not the script hash).
        plan.Findings.AddRange(checkouts.Validate(p, cmp));
        var mine = new HashSet<string>(p.SelectedDiffIds, StringComparer.OrdinalIgnoreCase);
        foreach (var other in repo.List().Where(o => o.Number != p.Number && o.TargetEnv.Equals(p.TargetEnv, StringComparison.OrdinalIgnoreCase)
                     && o.Status is PackageStatus.Draft or PackageStatus.Submitted or PackageStatus.Approved))
        {
            var shared = other.SelectedDiffIds.Where(mine.Contains).ToList();
            if (shared.Count > 0)
                plan.Findings.Add(Finding.Of(Severity.Warning, "PLAN012",
                    $"{other.Code} ({other.Status}, {other.Requester}) also changes {shared.Count} of these objects in {p.TargetEnv}: {string.Join(", ", shared.Take(3).Select(s => s.Split(':').Last()))}. Whichever deploys second overwrites the first.",
                    "Agree the order with the other requester, or merge the packages."));
        }
        return (cmp, plan);
    }

    public SourceArchiveService Sources => sources;
    public static string Script(TurnoverPackage p, ComparisonResult cmp, DeploymentPlan plan) => DeploymentPlanner.RenderScript(plan, cmp, p.Code);

    // ------------------------------------------------------------------ package lifecycle

    public async Task<TurnoverPackage> CreatePackageAsync(TurnoverUser user, string sourceKey, string targetKey, IEnumerable<string> logicals,
        IEnumerable<string> selectedIds, string title, string ticket, LibraryPair? explicitPair = null, CancellationToken ct = default, bool sourceOnly = false,
        string? programLibrary = null, string? sourceFileLibrary = null)
    {
        Require(user, Roles.Developer, "create a package");
        if (string.IsNullOrWhiteSpace(title)) throw new TurnoverException("A title is required.");
        var ids = selectedIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) throw new TurnoverException("Select at least one change.");

        // Re-run the comparison server-side: never trust ids or environment keys posted by the browser.
        var cmp = explicitPair is null ? await CompareAsync(sourceKey, targetKey, logicals, ct)
                                       : await CompareLibrariesAsync(sourceKey, explicitPair.SourceLibrary, explicitPair.TargetLibrary, ct, sourceOnly,
                                           programLibrary: programLibrary, sourceFileLibrary: sourceFileLibrary);
        var unknown = ids.Where(id => cmp.Find(id) is null).ToList();
        if (unknown.Count > 0) throw new TurnoverException($"{unknown.Count} selected change(s) no longer exist. Refresh the comparison.");

        return repo.Create(p =>
        {
            p.Title = title.Trim(); p.Ticket = (ticket ?? "").Trim(); p.Requester = user.Name; p.CreatedAt = clock.GetUtcNow();
            p.SourceEnv = cmp.Source.Key; p.TargetEnv = cmp.Target.Key;
            p.Logicals = cmp.Pairs.Select(x => x.Logical).ToList();
            if (explicitPair is not null) p.ExplicitPairs = [cmp.Pairs[0]];
            p.SourceOnly = sourceOnly;
            p.ProgramLibrary = programLibrary?.ToUpperInvariant();
            p.SourceFileLibrary = sourceFileLibrary?.ToUpperInvariant();
            p.SelectedDiffIds = ids;
            Audit(p, user, "Created", $"{ids.Count} change(s), {cmp.Source.Key} → {cmp.Target.Key}");
        });
    }

    public async Task<TurnoverPackage> UpdateSelectionAsync(TurnoverUser user, int number, IEnumerable<string> selectedIds, CancellationToken ct = default)
    {
        var p = Package(number);
        if (p.Status is not (PackageStatus.Draft or PackageStatus.Rejected)) throw new TurnoverException("Only a draft or rejected package can be edited.");
        RequireRequester(user, p);
        var (cmp, _) = await AnalyzeAsync(p, ct);
        var ids = selectedIds.Where(id => cmp.Find(id) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        p.SelectedDiffIds = ids;
        p.Status = PackageStatus.Draft;
        p.Approvals.Clear();
        Audit(p, user, "Selection changed", $"{ids.Count} change(s)");
        repo.Save(p);
        return p;
    }

    public async Task<TurnoverPackage> SubmitAsync(TurnoverUser user, int number, string? justification, CancellationToken ct = default)
    {
        var p = Package(number);
        if (p.Status is not (PackageStatus.Draft or PackageStatus.Rejected)) throw new TurnoverException($"Package is {p.Status}; only a draft can be submitted.");
        RequireRequester(user, p);
        var (_, plan) = await AnalyzeAsync(p, ct);
        // Somebody else's checked-out work is never overridden, in any environment.
        if (plan.Findings.FirstOrDefault(f => f.Code == "PLAN010") is { } locked)
            throw new TurnoverException(locked.Message);
        if (plan.HasOpenBlockers)
        {
            var target = _o.Env(p.TargetEnv);
            if (target.Tier == EnvironmentTier.Prod)
                throw new TurnoverException("Production packages cannot be submitted with open blockers. Fix them or untick the items.");
            if (!_o.AllowBlockerOverrideBelowProd)
                throw new TurnoverException("Blocker overrides are disabled (Turnover:AllowBlockerOverrideBelowProd).");
            if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < 20)
                throw new TurnoverException("The package has blockers. Overriding needs a written justification (20+ characters).");
            p.OverrideJustification = justification.Trim();
            Audit(p, user, "Blocker override", p.OverrideJustification);
        }
        p.Status = PackageStatus.Submitted;
        p.Approvals.Clear();
        p.SubmittedScriptHash = plan.ScriptHash;
        Audit(p, user, "Submitted", $"{plan.Steps.Count} steps, script {plan.ScriptHash}");
        repo.Save(p);
        return p;
    }

    public int ApprovalsRequired(TurnoverPackage p) =>
        _o.Env(p.TargetEnv).Tier == EnvironmentTier.Prod ? _o.ProdApprovalsRequired : _o.ApprovalsRequired;

    public async Task<TurnoverPackage> DecideAsync(TurnoverUser user, int number, bool approve, string comment, CancellationToken ct = default)
    {
        var p = Package(number);
        Require(user, Roles.Approver, "approve");
        if (p.Status != PackageStatus.Submitted) throw new TurnoverException($"Package is {p.Status}; only submitted packages can be approved or rejected.");
        if (p.Requester.Equals(user.Name, StringComparison.OrdinalIgnoreCase))
            throw new TurnoverException("Separation of duties: the requester cannot approve their own package.");
        if (p.Approvals.Any(a => a.User.Equals(user.Name, StringComparison.OrdinalIgnoreCase)))
            throw new TurnoverException("You have already recorded a decision on this package.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) throw new TurnoverException("Say why you are rejecting it.");

        p.Approvals.Add(new Approval { User = user.Name, Approved = approve, Comment = (comment ?? "").Trim(), At = clock.GetUtcNow() });
        if (!approve)
        {
            p.Status = PackageStatus.Rejected;
            Audit(p, user, "Rejected", comment ?? "");
        }
        else
        {
            Audit(p, user, "Approved", comment ?? "");
            if (p.Approvals.Count(a => a.Approved) >= ApprovalsRequired(p))
            {
                // Freeze exactly what was approved. Deploy refuses anything else.
                var (cmp, plan) = await AnalyzeAsync(p, ct, fresh: true);
                if (p.SubmittedScriptHash is not null && plan.ScriptHash != p.SubmittedScriptHash)
                {
                    // Approvers reviewed the submitted script; something changed since (target, source or selection). Start over.
                    p.Approvals.Clear();
                    p.SubmittedScriptHash = plan.ScriptHash;
                    Audit(p, user, "Approvals reset", $"script changed since submission ({plan.ScriptHash}); every approver must review it again");
                    repo.Save(p);
                    throw new TurnoverException("The script changed since it was submitted (the target, the source or the selection moved). Approvals were reset: review the current script and approve again.");
                }
                if (plan.Findings.FirstOrDefault(f => f.Code == "PLAN010") is { } held) throw new TurnoverException(held.Message);
                if (plan.HasOpenBlockers && (_o.Env(p.TargetEnv).Tier == EnvironmentTier.Prod || p.OverrideJustification is null))
                    throw new TurnoverException($"The package now has open blockers ({string.Join(", ", plan.Findings.Where(f => f.Severity == Severity.Blocker && !f.AutoFixed).Select(f => f.Code).Distinct())}). It cannot be approved; the requester must fix and re-submit.");
                p.ApprovedScriptHash = plan.ScriptHash;
                p.ApprovedTargetFingerprint = cmp.TargetFingerprint;
                p.ApprovedScript = Script(p, cmp, plan);
                p.ApprovedSteps = plan.Steps;
                // Replicas that will run this same script: remember their state so deploy can refuse if they drift.
                p.ApprovedReplicaFingerprints.Clear();
                foreach (var r in Replicas(p, DistributionMode.Deploy))
                {
                    var fp = (await catalog.LoadAsync(r, ct, refresh: true)).Fingerprint();
                    p.ApprovedReplicaFingerprints[r.Key.ToUpperInvariant()] = fp;
                    // "Identical" means no object differs, not equal fingerprints: a program rebuilt on the replica has its
                    // own creation time, which would otherwise block every later approval.
                    var vsPrimary = await CompareAsync(p.TargetEnv, r.Key, p.Logicals, ct);
                    if (vsPrimary.Differences.Count == 0) continue;
                    // Stage and Prod replicas must match: the same script on a drifted system can fail half-way or do the wrong thing.
                    if (_o.Env(p.TargetEnv).Tier >= EnvironmentTier.Stage)
                        throw new TurnoverException($"Replica {r.Key} is not identical to {p.TargetEnv}, so the approved script cannot be trusted there. Compare {p.TargetEnv} with {r.Key} and bring them in line first.");
                    Audit(p, user, "Replica differs", $"{r.Key} is not identical to {p.TargetEnv}; the approved script may fail there. Compare {p.TargetEnv} with {r.Key} before deploying.");
                }
                // Archive the exact source approvers signed: the target's current text and the incoming text.
                var archived = await sources.ArchiveForApprovalAsync(p, cmp, user.Name, ct);
                if (archived > 0) Audit(p, user, "Source archived", $"{archived} member version(s) (before and after)");
                p.Status = PackageStatus.Approved;
                Audit(p, user, "Fully approved", $"script {plan.ScriptHash}, target fingerprint {cmp.TargetFingerprint}");
            }
        }
        repo.Save(p);
        return p;
    }

    public async Task<DeploymentRun> DryRunAsync(TurnoverUser user, int number, CancellationToken ct = default)
    {
        var p = Package(number);
        if (!user.IsIn(Roles.Developer) && !user.IsIn(Roles.ReleaseManager)) throw new TurnoverException($"{user.Name} needs the Developer or ReleaseManager role to run a dry run.");
        if (p.Status is PackageStatus.Deploying or PackageStatus.RollingBack) throw new TurnoverException($"Package is {p.Status}.");
        var (_, plan) = await AnalyzeAsync(p, ct);
        var run = await simulator.RunAsync(_o.Env(p.TargetEnv), plan.Steps, "DryRun", user.Name, ct);
        p.Runs.Add(run);
        Audit(p, user, "Dry run", $"{plan.Steps.Count} steps");
        repo.Save(p);
        return run;
    }

    // ------------------------------------------------------------------ custom steps

    public TurnoverPackage AddCustomStep(TurnoverUser user, int number, CustomStepPoint point, StepKind kind, string title, string command, string? rollback)
    {
        var p = Package(number);
        EditableBy(user, p);
        if (!Enum.IsDefined(point)) throw new TurnoverException("Unknown step point.");
        var step = new CustomStep
        {
            Point = point, Kind = kind, Title = (title ?? "").Trim(), Command = (command ?? "").Trim(),
            Rollback = string.IsNullOrWhiteSpace(rollback) ? null : rollback.Trim(), AddedBy = user.Name, AddedAt = clock.GetUtcNow(),
        };
        if (DeploymentPlanner.ValidateCustom(step) is { } problem) throw new TurnoverException(problem);
        if (p.CustomSteps.Count >= 50) throw new TurnoverException("A package can hold at most 50 custom steps.");
        p.CustomSteps.Add(step);
        p.Status = PackageStatus.Draft;
        p.Approvals.Clear();
        Audit(p, user, "Custom step added", $"{point} · {kind} · {step.Title}");
        repo.Save(p);
        return p;
    }

    public TurnoverPackage RemoveCustomStep(TurnoverUser user, int number, string id)
    {
        var p = Package(number);
        EditableBy(user, p);
        var step = p.CustomSteps.FirstOrDefault(s => s.Id == id) ?? throw new TurnoverException("That custom step does not exist.");
        p.CustomSteps.Remove(step);
        p.Status = PackageStatus.Draft;
        p.Approvals.Clear();
        Audit(p, user, "Custom step removed", step.Title);
        repo.Save(p);
        return p;
    }

    private static void EditableBy(TurnoverUser user, TurnoverPackage p)
    {
        if (p.Status is not (PackageStatus.Draft or PackageStatus.Rejected)) throw new TurnoverException("Only a draft or rejected package can be edited.");
        RequireRequester(user, p);
    }

    // ------------------------------------------------------------------ deploy windows and scheduling

    /// <summary>Server-local time: deploy windows are written in it.</summary>
    public DateTime LocalNow() => Local(clock.GetUtcNow());
    public DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, clock.LocalTimeZone).DateTime;

    public IReadOnlyList<DeployWindow> Windows(string envKey) => DeployWindow.Parse(_o.Env(envKey).DeployWindows, envKey);

    /// <summary>Deploy automatically at <paramref name="whenUtc"/>, which must fall inside the target's deploy window.</summary>
    public TurnoverPackage Schedule(TurnoverUser user, int number, DateTimeOffset whenUtc)
    {
        var p = Package(number);
        CanDeploy(user, p);
        if (whenUtc <= clock.GetUtcNow().AddMinutes(1)) throw new TurnoverException("Pick a time at least a minute from now.");
        if (whenUtc > clock.GetUtcNow().AddDays(60)) throw new TurnoverException("Schedule at most 60 days ahead.");
        var windows = Windows(p.TargetEnv);
        if (!DeployWindow.IsOpen(windows, Local(whenUtc)))
            throw new TurnoverException($"{Local(whenUtc):ddd yyyy-MM-dd HH:mm} is outside {p.TargetEnv}'s deploy window ({string.Join("; ", windows)}).");
        p.ScheduledFor = whenUtc;
        p.ScheduledBy = user.Name;
        Audit(p, user, "Scheduled", $"{Local(whenUtc):ddd yyyy-MM-dd HH:mm} (server time)");
        repo.Save(p);
        return p;
    }

    public TurnoverPackage Unschedule(TurnoverUser user, int number)
    {
        var p = Package(number);
        Require(user, Roles.ReleaseManager, "cancel a schedule");
        if (p.ScheduledFor is null) throw new TurnoverException("The package is not scheduled.");
        p.ScheduledFor = null; p.ScheduledBy = null;
        Audit(p, user, "Schedule cancelled", "");
        repo.Save(p);
        return p;
    }

    /// <summary>Deploys every approved package whose scheduled time has come (called by the background scheduler).
    /// A refusal (target changed, window closed) is recorded on the package and the schedule is cleared; a busy target
    /// (another deployment running) leaves the schedule in place so the next pass tries again.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var n = 0;
        foreach (var due in repo.List().Where(x => x.Status == PackageStatus.Approved && x.ScheduledFor is { } at && at <= clock.GetUtcNow()).ToList())
        {
            var by = new TurnoverUser(due.ScheduledBy ?? "scheduler", [Roles.ReleaseManager]);
            try { await DeployAsync(by, due.Number, ct); n++; }
            catch (DeployBusyException) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad package must not stop the others in the same pass.
                var p = Package(due.Number);
                if (p.Status != PackageStatus.Approved) continue;   // the deploy itself recorded its failure
                p.ScheduledFor = null;
                Audit(p, by, "Scheduled deploy refused", ex.Message);
                try { repo.Save(p); } catch (PackageConflictException) { }
            }
        }
        return n;
    }

    // ------------------------------------------------------------------ deploy

    /// <summary>Two requests (a click and the scheduler) must never both move one package to Deploying.</summary>
    private static readonly SemaphoreSlim DeployGate = new(1, 1);
    /// <summary>Packages whose deploy or rollback is running in this process right now: never "stuck", so Mark failed
    /// and a second rollback are refused for them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> InFlight = new();

    private async Task Enter(CancellationToken ct)
    {
        if (!await DeployGate.WaitAsync(TimeSpan.FromSeconds(30), ct))
            throw new DeployBusyException("Another deployment or rollback is starting; try again in a moment.");
    }

    private void CanDeploy(TurnoverUser user, TurnoverPackage p)
    {
        Require(user, Roles.ReleaseManager, "deploy");
        if (p.Status != PackageStatus.Approved) throw new TurnoverException($"Package is {p.Status}; only an approved package can be deployed.");
        if (p.Requester.Equals(user.Name, StringComparison.OrdinalIgnoreCase) && _o.Env(p.TargetEnv).Tier >= EnvironmentTier.Stage)
            throw new TurnoverException("Separation of duties: the requester cannot deploy their own package to Stage or Prod.");
    }

    public IReadOnlyList<TurnoverEnvironment> Replicas(TurnoverPackage p, DistributionMode mode) =>
        _o.Env(p.TargetEnv).Distribution.Where(d => d.Mode == mode).Select(d => _o.Env(d.Environment)).ToList();

    /// <summary>Environments a package's script runs in: the target and its Deploy replicas.</summary>
    private HashSet<string> Touches(TurnoverPackage p) =>
        new[] { p.TargetEnv }.Concat(Replicas(p, DistributionMode.Deploy).Select(e => e.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>One deployment or rollback per environment at a time: two scripts interleaving (and one releasing the
    /// other's held job queue) is never safe.</summary>
    private void RequireIdle(TurnoverPackage p)
    {
        var mine = Touches(p);
        if (repo.List().FirstOrDefault(o => o.Number != p.Number && o.Status is PackageStatus.Deploying or PackageStatus.RollingBack && Touches(o).Overlaps(mine)) is { } busy)
            throw new DeployBusyException($"{busy.Code} is {busy.Status.ToString().ToLowerInvariant()} in {string.Join(", ", Touches(busy).Intersect(mine, StringComparer.OrdinalIgnoreCase))}. Wait for it to finish.");
    }

    /// <param name="emergency">Justification for deploying outside the target's deploy window (release manager only, 20+ characters).</param>
    /// <param name="ct">Cancels only the checks before anything runs. Once steps start they run to the end (or the first
    /// failure) and the Finally steps always run: a closed browser must never leave a half-done deployment unrecorded.</param>
    public async Task<DeploymentRun> DeployAsync(TurnoverUser user, int number, CancellationToken ct = default, string? emergency = null)
    {
        await Enter(ct);
        TurnoverPackage p;
        ComparisonResult cmp;
        try
        {
            p = Package(number);
            CanDeploy(user, p);
            RequireIdle(p);
            var windows = Windows(p.TargetEnv);
            if (!DeployWindow.IsOpen(windows, LocalNow()))
            {
                var next = DeployWindow.NextOpen(windows, LocalNow());
                if (string.IsNullOrWhiteSpace(emergency))
                    throw new TurnoverException($"{p.TargetEnv} only takes deployments in its window ({string.Join("; ", windows)}). Next opening: {next:ddd yyyy-MM-dd HH:mm}. Schedule it, or deploy as an emergency with a justification.");
                if (emergency.Trim().Length < 20) throw new TurnoverException("An emergency deployment needs a written justification (20+ characters).");
            }
            else emergency = null;

            (cmp, var plan) = await AnalyzeAsync(p, ct, fresh: true);
            if (cmp.TargetFingerprint != p.ApprovedTargetFingerprint)
                throw new TurnoverException($"{p.TargetEnv} has changed since approval (fingerprint {p.ApprovedTargetFingerprint} → {cmp.TargetFingerprint}). Re-submit so approvers see the current state.");
            if (plan.ScriptHash != p.ApprovedScriptHash)
                throw new TurnoverException("The script that would run is not the script that was approved (the source or the target changed). Re-submit for approval.");
            if (plan.Findings.FirstOrDefault(f => f.Code == "PLAN010") is { } held) throw new TurnoverException(held.Message);
            // Replicas that receive the same script must still be in the state they were in at approval.
            foreach (var r in Replicas(p, DistributionMode.Deploy))
            {
                var now = (await catalog.LoadAsync(r, ct, refresh: true)).Fingerprint();
                if (!p.ApprovedReplicaFingerprints.TryGetValue(r.Key.ToUpperInvariant(), out var then) || then != now)
                    throw new TurnoverException($"Replica {r.Key} has changed since approval (or was added to the distribution after it). Re-submit for approval.");
            }

            p.Status = PackageStatus.Deploying;
            p.ScheduledFor = null;
            if (emergency is not null) Audit(p, user, "Emergency deploy", $"outside the {p.TargetEnv} window: {emergency.Trim()}");
            Audit(p, user, "Deploy started", $"script {plan.ScriptHash}");
            repo.Save(p);
            InFlight[p.Number] = 0;   // after the save: a refused save must not leave the package marked as running
        }
        finally { DeployGate.Release(); }

        var mode = _o.IsLive ? "Live" : "Simulated";
        var runs = new List<DeploymentRun>();
        try
        {
            try { await RunDeploy(); }
            finally { InFlight.TryRemove(number, out _); }
        }
        catch (Exception ex)
        {
            // An executor should report, not throw; if one does, keep whatever runs finished so rollback can undo them.
            p = Package(number);
            p.Runs.AddRange(runs);
            if (p.Status == PackageStatus.Deploying) p.Status = PackageStatus.Failed;
            Audit(p, user, "Deploy failed", ex.Message);
            repo.Save(p);
            throw;
        }
        return await Finish();

        async Task RunDeploy()
        {
            var run = await executor.RunAsync(_o.Env(p.TargetEnv), p.ApprovedSteps, mode, user.Name, CancellationToken.None);
            run.Environment = p.TargetEnv;
            run.Emergency = emergency?.Trim();
            runs.Add(run);
            // The same approved script on each Deploy replica, only after the primary succeeded.
            if (run.Succeeded)
                foreach (var r in Replicas(p, DistributionMode.Deploy))
                {
                    var rr = await executor.RunAsync(r, p.ApprovedSteps, mode, user.Name, CancellationToken.None);
                    rr.Environment = r.Key;
                    rr.Emergency = run.Emergency;
                    runs.Add(rr);
                }
        }

        async Task<DeploymentRun> Finish()
        {
        // Record the outcome first: nothing after this point may leave the package stuck in Deploying.
        p = Package(number);
        p.Runs.AddRange(runs);
        var ok = runs.All(r => r.Succeeded);
        // Someone may have changed the status meanwhile (Mark failed from another server): keep their decision, add the runs.
        if (p.Status == PackageStatus.Deploying) p.Status = ok ? PackageStatus.Deployed : PackageStatus.Failed;
        else ok = false;
        foreach (var rr in runs.Skip(1))
            Audit(p, user, rr.Succeeded ? "Deployed to replica" : "Replica deploy failed",
                $"{rr.Environment}: " + (rr.Succeeded ? $"{rr.Results.Count} steps" : rr.Results.FirstOrDefault(r => r.Status == StepStatus.Failed)?.Message ?? ""));
        if (ok && Replicas(p, DistributionMode.Verify).Count > 0)
            Audit(p, user, "Replicas to verify", string.Join(", ", Replicas(p, DistributionMode.Verify).Select(e => e.Key)) + " (replicated by HA: use Verify replicas once replication has caught up)");
        Audit(p, user, runs[0].Succeeded ? "Deployed" : "Deploy failed",
            runs[0].Succeeded ? $"{runs[0].Results.Count} steps" : runs[0].Results.FirstOrDefault(r => r.Status == StepStatus.Failed)?.Message ?? "");
        repo.Save(p);

        if (ok)
        {
            // Bookkeeping after the fact: a failure here is recorded, never turned into a failed deployment.
            try
            {
                // Live deploys change the target's source: archive what it holds now. (Simulated runs change nothing.)
                if (_o.IsLive)
                {
                    var n = await sources.ArchiveDeployedAsync(p, cmp, user.Name, CancellationToken.None);
                    if (n > 0) Audit(p, user, "Source archived", $"{n} deployed member version(s)");
                }
                if (_o.Env(p.TargetEnv).Tier >= _o.Checkout.ReleaseOnTier)
                {
                    var released = checkouts.ReleaseForPackage(p, cmp, user.Name);
                    if (released > 0) Audit(p, user, "Check-outs released", $"{released} member(s) checked in after reaching {p.TargetEnv}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Audit(p, user, "After-deploy bookkeeping failed", $"{ex.Message} (the deployment itself succeeded)");
            }
            try { repo.Save(p); } catch (PackageConflictException) { }
        }
        // The caller sees the first failure anywhere (a replica included), not just the primary's result.
        return runs.FirstOrDefault(r => !r.Succeeded) ?? runs[0];
        }
    }

    /// <summary>
    /// Recovery for a package left in Deploying or RollingBack because the app stopped mid-run. Check what ran on IBM i
    /// first (Runs tab and the objects); then this marks it Failed so it can be rolled back or fixed by hand.
    /// </summary>
    public TurnoverPackage MarkFailed(TurnoverUser user, int number, string note)
    {
        Require(user, Roles.ReleaseManager, "mark a deployment as failed");
        var p = Package(number);
        if (p.Status is not (PackageStatus.Deploying or PackageStatus.RollingBack)) throw new TurnoverException($"Package is {p.Status}; only a package stuck in Deploying or RollingBack can be marked failed.");
        if (InFlight.ContainsKey(number)) throw new TurnoverException($"{p.Code} is still running on this server; wait for it to finish.");
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length < 20) throw new TurnoverException("Say what you checked on IBM i (20+ characters).");
        p.Status = PackageStatus.Failed;
        Audit(p, user, "Marked failed", note.Trim());
        repo.Save(p);
        return p;
    }

    /// <summary>
    /// Compares the target with each distribution replica and reports, per object the package changed, whether the
    /// replica has it too. Used for HA-replicated systems (Verify) and to confirm Deploy replicas.
    /// </summary>
    public async Task<IReadOnlyList<DeploymentRun>> VerifyReplicasAsync(TurnoverUser user, int number, CancellationToken ct = default)
    {
        var p = Package(number);
        if (!user.IsIn(Roles.ReleaseManager) && !user.IsIn(Roles.Approver)) throw new TurnoverException($"{user.Name} needs the ReleaseManager or Approver role to verify replicas.");
        if (p.Status != PackageStatus.Deployed) throw new TurnoverException("Only a deployed package can be verified on its replicas.");
        var replicas = Replicas(p, DistributionMode.Verify).Concat(Replicas(p, DistributionMode.Deploy)).ToList();
        if (replicas.Count == 0) throw new TurnoverException($"{p.TargetEnv} has no distribution targets.");
        var runs = new List<DeploymentRun>();
        foreach (var r in replicas)
        {
            var run = new DeploymentRun { Mode = "Verify", Environment = r.Key, User = user.Name, StartedAt = clock.GetUtcNow(), Succeeded = true };
            var cmp = await CompareAsync(p.TargetEnv, r.Key, p.Logicals, ct, refresh: true);
            var order = 0;
            foreach (var id in p.SelectedDiffIds)
            {
                var d = cmp.Find(id);
                run.Results.Add(new StepResult
                {
                    Order = ++order, Title = id.Split(':').Last(),
                    Status = d is null ? StepStatus.Succeeded : StepStatus.Failed,
                    Message = d is null ? $"Same in {p.TargetEnv} and {r.Key}." : $"{r.Key} differs ({d.Change}){(d.Details.Count > 0 ? ": " + string.Join("; ", d.Details.Take(2)) : "")}",
                });
                if (d is not null) run.Succeeded = false;
            }
            run.EndedAt = clock.GetUtcNow();
            runs.Add(run);
        }
        p = Package(number);
        foreach (var run in runs)
        {
            p.Runs.Add(run);
            Audit(p, user, run.Succeeded ? "Replica verified" : "Replica not in step", $"{run.Environment}: {run.Results.Count(x => x.Status == StepStatus.Failed)} of {run.Results.Count} object(s) differ");
        }
        repo.Save(p);
        return runs;
    }

    public async Task<DeploymentRun> RollbackAsync(TurnoverUser user, int number, CancellationToken ct = default)
    {
        Require(user, Roles.ReleaseManager, "roll back");
        await Enter(ct);
        TurnoverPackage p;
        PackageStatus before;
        try
        {
            p = Package(number);
            if (p.Status is not (PackageStatus.Deployed or PackageStatus.Failed)) throw new TurnoverException($"Package is {p.Status}; nothing to roll back.");
            RequireIdle(p);
            if (InFlight.ContainsKey(number)) throw new DeployBusyException($"{p.Code} is still running on this server.");
            before = p.Status;
            // RollingBack stops a double click (or a second release manager) from running every undo twice.
            p.Status = PackageStatus.RollingBack;
            Audit(p, user, "Rollback started", "");
            repo.Save(p);
            InFlight[number] = 0;
        }
        finally { DeployGate.Release(); }

        var runs = new List<DeploymentRun>();
        try
        {
            // Every environment the approved script ran in: the target and its Deploy replicas, newest run of each.
            var envs = new[] { p.TargetEnv }.Concat(p.Runs.Where(r => r.Mode is "Live" or "Simulated" && r.Environment is not null).Select(r => r.Environment!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var env in envs)
            {
                var last = p.Runs.LastOrDefault(r => r.Mode is "Live" or "Simulated" && string.Equals(r.Environment ?? p.TargetEnv, env, StringComparison.OrdinalIgnoreCase));
                if (last is null) continue;
                var done = last.Results.Where(r => r.Status == StepStatus.Succeeded || r.Status == StepStatus.Simulated).Select(r => r.Order).ToHashSet();
                // Undo the frozen, approved steps that ran. Never re-derive them: after a deploy the environments match,
                // so a fresh comparison would produce an empty plan and the rollback would silently do nothing.
                // A retry after an incomplete rollback skips the undos that already succeeded (re-running "DROP COLUMN"
                // or "re-create index" would fail and keep the package Failed for ever).
                var undone = p.Runs.Skip(p.Runs.IndexOf(last) + 1)
                    .Where(r => r.Mode == "Rollback" && string.Equals(r.Environment ?? p.TargetEnv, env, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(r => r.Results).Where(r => r.Status is StepStatus.Succeeded or StepStatus.Simulated or StepStatus.Skipped).Select(r => r.Order).ToHashSet();
                var steps = p.ApprovedSteps.Where(s => done.Contains(s.Order) && !s.AlwaysRun && !undone.Contains(s.Order)).ToList();
                if (steps.Count == 0) continue;
                var run = await executor.RollbackAsync(_o.Env(env), steps, user.Name, CancellationToken.None);
                run.Environment = env;
                runs.Add(run);
            }
        }
        catch (Exception ex)
        {
            InFlight.TryRemove(number, out _);
            p = Package(number);
            p.Runs.AddRange(runs);
            p.Status = PackageStatus.Failed;
            Audit(p, user, "Rollback failed", ex.Message);
            repo.Save(p);
            throw;
        }

        InFlight.TryRemove(number, out _);
        p = Package(number);
        if (runs.Count == 0)
        {
            p.Status = before;
            Audit(p, user, "Rollback not needed", "No completed steps recorded for the last deployment.");
            repo.Save(p);
            throw new TurnoverException("No completed steps recorded for the last deployment.");
        }
        p.Runs.AddRange(runs);
        var ok = runs.All(r => r.Succeeded);
        // A rollback with failed undo steps is not "rolled back": stay Failed so someone looks and can try again.
        p.Status = ok ? PackageStatus.RolledBack : PackageStatus.Failed;
        Audit(p, user, ok ? "Rolled back" : "Rollback incomplete", ok
            ? $"All undo steps succeeded or were manual ({string.Join(", ", runs.Select(r => r.Environment))})."
            : $"Some undo steps failed in {string.Join(", ", runs.Where(r => !r.Succeeded).Select(r => r.Environment))}; see the run log.");
        repo.Save(p);
        return runs.FirstOrDefault(r => !r.Succeeded) ?? runs[0];
    }

    // ------------------------------------------------------------------ helpers

    private static void Require(TurnoverUser user, string role, string action)
    {
        if (!user.IsIn(role)) throw new TurnoverException($"{user.Name} needs the {role} role to {action}.");
    }

    private static void RequireRequester(TurnoverUser user, TurnoverPackage p)
    {
        if (!p.Requester.Equals(user.Name, StringComparison.OrdinalIgnoreCase))
            throw new TurnoverException("Only the requester can change or submit this package.");
    }

    private void Audit(TurnoverPackage p, TurnoverUser user, string action, string detail) =>
        p.Audit.Add(new AuditEntry { At = clock.GetUtcNow(), User = user.Name, Action = action, Detail = detail });
}


/// <summary>Another deployment or rollback is running on the same environment; try again later (a schedule stays).</summary>
public sealed class DeployBusyException(string message) : TurnoverException(message);
