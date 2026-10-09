namespace IbmTurnover.Core.Models;

public enum PackageStatus { Draft, Submitted, Approved, Rejected, Deploying, Deployed, Failed, RolledBack, RollingBack }

/// <summary>Finally: run after everything else, also when a step failed (release job queues, restart subsystems).</summary>
public enum StepPhase { Setup = 0, PreCheck = 1, Backup = 2, Deploy = 3, Compile = 4, Routines = 5, Security = 6, PostCheck = 7, Finally = 8 }

/// <summary>CopySource / CopyIfs: a source member or stream file copied between partitions by the deploy engine (two connections).</summary>
public enum StepKind { Sql, Cl, Check, CopySource, CopyIfs }

/// <summary>Where a package's own conversion step runs.</summary>
public enum CustomStepPoint
{
    /// <summary>After the backups, before any object changes.</summary>
    BeforeChanges,
    /// <summary>After tables, columns, indexes and keys change; before programs are rebuilt (data conversion).</summary>
    AfterTables,
    /// <summary>After everything, before the post-checks.</summary>
    AfterAll,
}

/// <summary>A step the requester adds to the package (data conversion, a one-off CL command, an extra check).
/// It is part of the approved script and its hash like every generated step.</summary>
public sealed class CustomStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public CustomStepPoint Point { get; set; }
    /// <summary>Sql, Cl or Check (a SELECT that must return 0).</summary>
    public StepKind Kind { get; set; } = StepKind.Sql;
    public string Title { get; set; } = "";
    public string Command { get; set; } = "";
    public string? Rollback { get; set; }
    public string AddedBy { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class PlanStep
{
    public int Order { get; set; }
    public StepPhase Phase { get; set; }
    public StepKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? DiffId { get; set; }
    /// <summary>SQL statement, CL command, or (Check) a SELECT COUNT(*) that must return 0.</summary>
    public string Command { get; set; } = "";
    public string? Rollback { get; set; }
    public bool Destructive { get; set; }
    /// <summary>Lower runs first inside the same phase.</summary>
    public int Rank { get; set; }
    /// <summary>CopySource only: environment to read from, and LIB/FILE(MBR) on each side.</summary>
    public string? SourceEnvironment { get; set; }
    public string? CopyFrom { get; set; }
    public string? CopyTo { get; set; }
    /// <summary>CopyIfs: CCSID the stream file is created with on the target.</summary>
    public int? Ccsid { get; set; }
    /// <summary>Runs even after an earlier step failed (Finally phase).</summary>
    public bool AlwaysRun { get; set; }
    /// <summary>Only run when the step with this Order succeeded (the undo of a pre-deploy command: never release a
    /// job queue this deployment did not hold).</summary>
    public int? RunIfSucceeded { get; set; }
}

public sealed class DeploymentPlan
{
    public string BackupLibrary { get; set; } = "";
    public List<PlanStep> Steps { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
    public string ScriptHash { get; set; } = "";
    public bool HasOpenBlockers => Findings.Any(f => f.Severity == Severity.Blocker && !f.AutoFixed);
}

public static class Roles
{
    public const string Developer = "Developer";
    public const string Approver = "Approver";
    public const string ReleaseManager = "ReleaseManager";
}

public sealed record TurnoverUser(string Name, IReadOnlyCollection<string> Roles)
{
    public bool IsIn(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public sealed class Approval
{
    public string User { get; set; } = "";
    public bool Approved { get; set; }
    public string Comment { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class AuditEntry
{
    public DateTimeOffset At { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

public enum StepStatus { Succeeded, Failed, Skipped, Simulated }

public sealed class StepResult
{
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public StepStatus Status { get; set; }
    public string Message { get; set; } = "";
    public long DurationMs { get; set; }
}

public sealed class DeploymentRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>DryRun, Live, Simulated, Rollback, Verify.</summary>
    public string Mode { get; set; } = "DryRun";
    /// <summary>Environment the run went to. Null = the package's target (older runs); a distribution replica otherwise.</summary>
    public string? Environment { get; set; }
    /// <summary>Deploy started outside the environment's window: who allowed it and why.</summary>
    public string? Emergency { get; set; }
    public string User { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public bool Succeeded { get; set; }
    public List<StepResult> Results { get; set; } = new();
}

public sealed class TurnoverPackage
{
    public int Number { get; set; }
    /// <summary>Incremented on every save; a save from a stale copy is refused (two people acting at once).</summary>
    public int Version { get; set; }
    public string Title { get; set; } = "";
    public string Ticket { get; set; } = "";
    public string Requester { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string SourceEnv { get; set; } = "";
    public string TargetEnv { get; set; } = "";
    public List<string> Logicals { get; set; } = new();
    public List<string> SelectedDiffIds { get; set; } = new();
    public PackageStatus Status { get; set; } = PackageStatus.Draft;
    public string? OverrideJustification { get; set; }
    public string? ApprovedScriptHash { get; set; }
    /// <summary>Script hash when submitted: approvers review this script; the freeze refuses a different one.</summary>
    public string? SubmittedScriptHash { get; set; }
    public string? ApprovedTargetFingerprint { get; set; }
    public string? ApprovedScript { get; set; }
    /// <summary>The exact steps approvers signed off. Deploy runs these; rollback undoes these.</summary>
    public List<PlanStep> ApprovedSteps { get; set; } = new();
    /// <summary>Set for a library-to-library package inside one environment.</summary>
    public List<LibraryPair> ExplicitPairs { get; set; } = new();
    /// <summary>Developer-library package: only objects present in the source library are considered.</summary>
    public bool SourceOnly { get; set; }
    public string? ProgramLibrary { get; set; }
    public string? SourceFileLibrary { get; set; }
    /// <summary>Conversion / one-off steps written by the requester.</summary>
    public List<CustomStep> CustomSteps { get; set; } = new();
    /// <summary>Distribution Deploy replicas: their fingerprint at approval (they must still match at deploy).</summary>
    public Dictionary<string, string> ApprovedReplicaFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Deploy automatically at this time (UTC), inside the target's deploy window.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }
    public string? ScheduledBy { get; set; }
    public List<Approval> Approvals { get; set; } = new();
    public List<AuditEntry> Audit { get; set; } = new();
    public List<DeploymentRun> Runs { get; set; } = new();

    public string Code => $"TOV-{Number:D5}";
    public string BackupLibrary => $"TB{Number:D8}";
}
