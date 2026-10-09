using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Planning;

/// <summary>
/// Turns the selected differences into an ordered, re-runnable deployment: setup → pre-checks (must
/// return 0) → backups → DDL in dependency order → source copy + compile → routines/views/triggers →
/// post-checks. Every step carries its own rollback statement.
/// </summary>
public sealed class DeploymentPlanner
{
    /// <param name="custom">The package's own conversion steps; placed at their point and hashed with the rest.</param>
    public DeploymentPlan Build(ComparisonResult cmp, IReadOnlyCollection<string> selectedIds, string packageCode, string backupLibrary, IReadOnlyList<CustomStep>? custom = null)
    {
        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        var plan = new DeploymentPlan { BackupLibrary = backupLibrary };
        Validate(cmp, selected, plan);
        custom ??= [];
        if (selected.Count == 0 && custom.Count > 0) plan.Findings.RemoveAll(f => f.Code == "PLAN004");
        foreach (var cs in custom)
            if (ValidateCustom(cs) is { } problem) plan.Findings.Add(Finding.Of(Severity.Blocker, "PLAN020", $"Custom step '{cs.Title}': {problem}"));
        if (custom.Count > 0)
            plan.Findings.Add(Finding.Of(Severity.Info, "PLAN021", $"{custom.Count} custom step(s) written by the requester run as part of this script. Approvers: read them."));

        var diffs = cmp.Differences.Where(d => selected.Contains(d.Id)).ToList();
        // A level-check recompile only matters if one of the table changes that cause it ships.
        diffs.RemoveAll(d => IsLevelCheckOnly(d) && !d.DependsOn.Any(selected.Contains));

        var b = new Builder(cmp, backupLibrary, packageCode);
        foreach (var d in diffs) b.Emit(d);
        foreach (var cs in custom.Where(x => ValidateCustom(x) is null)) b.Custom(cs);
        plan.Steps = b.Finish(diffs);
        plan.ScriptHash = Hash(RenderScript(plan, cmp, packageCode));
        return plan;
    }

    /// <summary>Null when the step is acceptable; otherwise why not.</summary>
    public static string? ValidateCustom(CustomStep s)
    {
        if (string.IsNullOrWhiteSpace(s.Title) || s.Title.Length > 100) return "a title of 1-100 characters is required.";
        if (s.Kind is not (StepKind.Sql or StepKind.Cl or StepKind.Check)) return "kind must be SQL, CL or Check.";
        var cmd = (s.Command ?? "").Trim().TrimEnd(';').Trim();
        if (cmd.Length == 0 || cmd.Length > 5000) return "the command must be 1-5000 characters.";
        if (cmd.StartsWith("--", StringComparison.Ordinal)) return "the command is only a comment.";
        if (s.Kind != StepKind.Cl && cmd.Contains(';')) return "one SQL statement per step (no ';' inside).";
        if (s.Kind == StepKind.Cl && (cmd.Contains('\n') || cmd.Contains('\r'))) return "one CL command per step, on one line.";
        if (s.Kind == StepKind.Cl && cmd.Count(ch => ch == '\'') % 2 != 0) return "unbalanced quotes.";
        if (s.Kind == StepKind.Check && !cmd.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return "a check is a SELECT that returns 0 when all is well.";
        if (s.Rollback is { Length: > 5000 }) return "the undo command is too long.";
        return null;
    }

    public static bool IsLevelCheckOnly(Difference d) =>
        d.Kind is ObjectKind.Program or ObjectKind.ServiceProgram && d.Change == ChangeKind.Drift;

    private static void Validate(ComparisonResult cmp, HashSet<string> selected, DeploymentPlan plan)
    {
        plan.Findings.AddRange(cmp.GlobalFindings.Where(f => f.Severity != Severity.Info));
        if (selected.Count == 0)
            plan.Findings.Add(Finding.Of(Severity.Blocker, "PLAN004", "Nothing is selected."));

        var unknown = selected.Where(id => cmp.Find(id) is null).ToList();
        if (unknown.Count > 0)
            plan.Findings.Add(Finding.Of(Severity.Blocker, "PLAN005",
                $"{unknown.Count} selected item(s) no longer differ between the environments ({string.Join(", ", unknown.Take(3))}). Someone changed source or target since the package was built.",
                "Re-analyze and rebuild the selection."));

        foreach (var d in cmp.Differences.Where(x => selected.Contains(x.Id)))
        {
            foreach (var f in d.Findings.Where(f => f.Severity == Severity.Blocker && !f.AutoFixed))
                plan.Findings.Add(Finding.Of(Severity.Blocker, f.Code, $"{d.Kind} {d.DisplayName}: {f.Message}", f.Remedy));

            if (IsLevelCheckOnly(d)) continue;
            foreach (var dep in d.DependsOn.Distinct())
            {
                var other = cmp.Find(dep);
                if (other is not null && !selected.Contains(dep))
                    plan.Findings.Add(Finding.Of(Severity.Blocker, "PLAN001",
                        $"{d.Kind} {d.DisplayName} needs {other.Kind} {other.DisplayName}, which is not selected. The step would fail.",
                        $"Tick {other.DisplayName} as well, or untick {d.DisplayName}."));
            }
        }

        // The reverse: a table change that ships without recompiling the programs that open the table.
        foreach (var rc in cmp.Differences.Where(x => IsLevelCheckOnly(x) && !selected.Contains(x.Id)))
            if (rc.DependsOn.Any(selected.Contains))
                plan.Findings.Add(Finding.Of(Severity.Blocker, "PLAN003",
                    $"{rc.TargetLibrary}/{rc.Name} is not being recompiled, but a table it opens changes format. It will end with CPF4131 (level check).",
                    $"Tick the recompile of {rc.Name}."));
    }

    public static string RenderScript(DeploymentPlan plan, ComparisonResult cmp, string packageCode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- ==========================================================================");
        sb.AppendLine($"-- {packageCode}   {cmp.Source.Key} → {cmp.Target.Key}");
        sb.AppendLine($"-- Libraries: {string.Join(", ", cmp.Pairs.Select(p => $"{p.SourceLibrary}→{p.TargetLibrary}"))}");
        sb.AppendLine($"-- Backup library: {plan.BackupLibrary}");
        sb.AppendLine("-- Run as one unit in ACS Run SQL Scripts (stop on error) or through the Turnover deploy engine.");
        sb.AppendLine("-- CHECK steps must return 0. Anything else means STOP.");
        sb.AppendLine("-- ==========================================================================");
        foreach (var s in plan.Steps)
        {
            sb.AppendLine();
            sb.AppendLine($"-- [{s.Order:D3}] {s.Phase} · {s.Title}{(s.Destructive ? "  (DESTRUCTIVE)" : "")}{(s.AlwaysRun ? "  (ALWAYS RUNS, also after a failure)" : "")}");
            sb.AppendLine(AsSql(s) + ";");
        }
        return sb.ToString();
    }

    public static string RenderRollback(IEnumerable<PlanStep> steps, string packageCode)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- {packageCode} ROLLBACK — reverse order of the deployment. Run only the part that was applied.");
        foreach (var s in steps.Where(x => x.Rollback is not null).OrderByDescending(x => x.Order))
        {
            sb.AppendLine();
            sb.AppendLine($"-- undo [{s.Order:D3}] {s.Title}");
            var undo = s.Rollback!.Trim().TrimEnd(';');
            sb.AppendLine(IsManual(undo) ? undo : undo + ";");
        }
        return sb.ToString();
    }

    /// <summary>A rollback that is only a comment is an instruction for a person, not something to execute.</summary>
    public static bool IsManual(string rollback) => rollback.TrimStart().StartsWith("--", StringComparison.Ordinal);

    public static string AsSql(PlanStep s) => s.Kind == StepKind.Cl ? Ddl.Qcmdexc(s.Command) : s.Command.Trim().TrimEnd(';');

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];

    private sealed class Builder(ComparisonResult cmp, string backupLib, string packageCode)
    {
        private readonly List<PlanStep> _steps = new();
        private readonly Dictionary<string, string> _backedUp = new(StringComparer.OrdinalIgnoreCase); // LIB/OBJ/TYPE -> backup name
        private readonly HashSet<string> _backupNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _needsLibl;
        private readonly HashSet<string> _createdMsgf = new(StringComparer.OrdinalIgnoreCase);

        private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private void Step(StepPhase phase, int rank, StepKind kind, string title, string command, string? rollback = null, Difference? d = null, bool destructive = false) =>
            _steps.Add(new PlanStep { Phase = phase, Rank = rank, Kind = kind, Title = title, Command = command, Rollback = rollback, DiffId = d?.Id, Destructive = destructive });

        private static string Cl(string cl) => Ddl.Qcmdexc(cl);

        public void Emit(Difference d)
        {
            foreach (var pc in d.PreChecks)
                Step(StepPhase.PreCheck, 0, StepKind.Check, pc.Title, pc.Sql, d: d);
            Locks(d);

            var lib = d.TargetLibrary;
            switch (d.Kind, d.Change)
            {
                case (ObjectKind.Library, ChangeKind.Added):
                    Step(StepPhase.Setup, 5, StepKind.Sql, $"Create library {lib}", $"CREATE SCHEMA {lib}", $"DROP SCHEMA {lib} RESTRICT", d);
                    break;

                case (ObjectKind.Sequence, ChangeKind.Added):
                    Step(StepPhase.Deploy, 10, StepKind.Sql, $"Create sequence {d.Name}", d.SourceDdl!, $"DROP SEQUENCE {Ddl.Q(lib, d.Name)} RESTRICT", d);
                    PostExists(d, "SYSSEQUENCES", "SEQUENCE_SCHEMA", "SEQUENCE_NAME");
                    break;
                case (ObjectKind.Sequence, ChangeKind.Modified or ChangeKind.Drift):
                {
                    var s = (SequenceDef)d.Source!; var t = (SequenceDef)d.Target!;
                    if (d.Details.Any(x => !x.StartsWith("Next value") && !x.StartsWith("Type")))
                        Step(StepPhase.Deploy, 10, StepKind.Sql, $"Align attributes of {d.Name}", Ddl.AlterSequenceAttributes(lib, s), Ddl.AlterSequenceAttributes(lib, t), d);
                    if (d.Findings.FirstOrDefault(f => f.Code == "SEQ003") is not null)
                    {
                        var restart = d.RestartWith ?? throw new TurnoverException($"SEQ003 on {d.Name} without a restart value.");
                        Step(StepPhase.Deploy, 11, StepKind.Sql, $"Move {d.Name} past existing keys", $"ALTER SEQUENCE {Ddl.Q(lib, d.Name)} RESTART WITH {restart}", null, d);
                    }
                    break;
                }
                case (ObjectKind.Sequence, ChangeKind.Removed):
                    Step(StepPhase.Deploy, 90, StepKind.Sql, $"Drop sequence {d.Name}", $"DROP SEQUENCE {Ddl.Q(lib, d.Name)} RESTRICT", d.TargetDdl, d, true);
                    break;

                case (ObjectKind.Table, ChangeKind.Added):
                {
                    var t = (TableDef)d.Source!;
                    Step(StepPhase.Deploy, 20, StepKind.Sql, $"Create table {d.Name}", d.SourceDdl!, $"DROP TABLE {Ddl.Q(lib, d.Name)} RESTRICT", d);
                    // No STRJRNPF here: CREATE TABLE in an SQL schema is journaled to QSQJRN automatically, and STRJRNPF on
                    // an already journaled file ends with CPF7030, which would stop the deployment.
                    _ = t;
                    PostExists(d, "SYSTABLES", "TABLE_SCHEMA", "TABLE_NAME");
                    break;
                }
                case (ObjectKind.Table, ChangeKind.Modified):
                {
                    var s = (TableDef)d.Source!; var t = (TableDef)d.Target!;
                    if (!Eq(s.SystemName, t.SystemName))
                    {
                        Backup(lib, t.SystemName, "*FILE", true);
                        Step(StepPhase.Deploy, 19, StepKind.Sql, $"Rename system name of {d.Name} to {s.SystemName}",
                            $"RENAME TABLE {Ddl.Q(lib, d.Name)} TO SYSTEM NAME {s.SystemName}", $"RENAME TABLE {Ddl.Q(lib, d.Name)} TO SYSTEM NAME {t.SystemName}", d);
                    }
                    if (s.Journaled && !t.Journaled)
                        Step(StepPhase.Deploy, 21, StepKind.Cl, $"Start journaling {d.Name}",
                            $"STRJRNPF FILE({lib}/{s.SystemName}) JRN({lib}/QSQJRN) IMAGES(*BOTH) OMTJRNE(*OPNCLO)", Cl($"ENDJRNPF FILE({lib}/{s.SystemName})"), d);
                    if (!Eq(s.Text, t.Text))
                        Step(StepPhase.Deploy, 35, StepKind.Sql, $"Table text of {d.Name}", Ddl.Label($"TABLE {Ddl.Q(lib, d.Name)}", s.Text), Ddl.Label($"TABLE {Ddl.Q(lib, d.Name)}", t.Text), d);
                    break;
                }
                case (ObjectKind.Table, ChangeKind.Removed):
                {
                    var t = (TableDef)d.Target!;
                    Backup(lib, t.SystemName, "*FILE", true);
                    Step(StepPhase.Deploy, 95, StepKind.Sql, $"Drop table {d.Name}", $"DROP TABLE {Ddl.Q(lib, d.Name)} RESTRICT",
                        Cl($"CRTDUPOBJ OBJ({BackupName(lib, t.SystemName, "*FILE")}) FROMLIB({backupLib}) OBJTYPE(*FILE) TOLIB({lib}) NEWOBJ({t.SystemName}) DATA(*YES)"), d, true);
                    break;
                }

                case (ObjectKind.Column, _):
                    EmitColumn(d);
                    break;

                case (ObjectKind.Index, ChangeKind.Added):
                    Step(StepPhase.Deploy, 40, StepKind.Sql, $"Create index {d.Name}", d.SourceDdl!, $"DROP INDEX {Ddl.Q(lib, d.Name)}", d);
                    break;
                case (ObjectKind.Index, ChangeKind.Modified):
                    Step(StepPhase.Deploy, 26, StepKind.Sql, $"Drop index {d.Name} (rebuilt below)", $"DROP INDEX {Ddl.Q(lib, d.Name)}", d.TargetDdl, d);
                    Step(StepPhase.Deploy, 40, StepKind.Sql, $"Rebuild index {d.Name}", d.SourceDdl!, $"DROP INDEX {Ddl.Q(lib, d.Name)}", d);
                    break;
                case (ObjectKind.Index, ChangeKind.Removed):
                    Step(StepPhase.Deploy, 90, StepKind.Sql, $"Drop index {d.Name}", $"DROP INDEX {Ddl.Q(lib, d.Name)}", d.TargetDdl, d, true);
                    break;

                case (ObjectKind.ForeignKey, ChangeKind.Added):
                {
                    var fk = (ForeignKeyDef)d.Source!;
                    Step(StepPhase.Deploy, 50, StepKind.Sql, $"Add foreign key {d.Name}", d.SourceDdl!, Ddl.DropConstraint(lib, fk.Table, fk.Name), d);
                    break;
                }
                case (ObjectKind.ForeignKey, ChangeKind.Modified):
                {
                    var s = (ForeignKeyDef)d.Source!; var t = (ForeignKeyDef)d.Target!;
                    Step(StepPhase.Deploy, 25, StepKind.Sql, $"Drop foreign key {t.Name} (re-added below)", Ddl.DropConstraint(lib, t.Table, t.Name), d.TargetDdl, d);
                    Step(StepPhase.Deploy, 50, StepKind.Sql, $"Re-add foreign key {s.Name}", d.SourceDdl!, Ddl.DropConstraint(lib, s.Table, s.Name), d);
                    break;
                }
                case (ObjectKind.ForeignKey, ChangeKind.Removed):
                {
                    var t = (ForeignKeyDef)d.Target!;
                    Step(StepPhase.Deploy, 25, StepKind.Sql, $"Drop foreign key {t.Name}", Ddl.DropConstraint(lib, t.Table, t.Name), d.TargetDdl, d, true);
                    break;
                }

                case (ObjectKind.CheckConstraint, _):
                {
                    var q = Ddl.Q(lib, d.Parent!);
                    var s = d.Source as CheckConstraintDef; var t = d.Target as CheckConstraintDef;
                    string Add(CheckConstraintDef ck) => $"ALTER TABLE {q} ADD CONSTRAINT {Ddl.C(ck.Name)} CHECK ({ck.Clause})";
                    string Drop(CheckConstraintDef ck) => $"ALTER TABLE {q} DROP CONSTRAINT {Ddl.C(ck.Name)}";
                    if (t is not null) Step(StepPhase.Deploy, 25, StepKind.Sql, $"Drop check {t.Name}", Drop(t), Add(t), d, s is null);
                    if (s is not null) Step(StepPhase.Deploy, 52, StepKind.Sql, $"Add check {s.Name} on {d.Parent}", Add(s), Drop(s), d);
                    break;
                }
                case (ObjectKind.Alias, ChangeKind.Removed):
                    Step(StepPhase.Deploy, 90, StepKind.Sql, $"Drop alias {d.Name}", $"DROP ALIAS {Ddl.Q(lib, d.Name)}", d.TargetDdl, d, true);
                    break;
                case (ObjectKind.Alias, _):
                    Step(StepPhase.Deploy, 45, StepKind.Sql, $"{(d.Target is null ? "Create" : "Re-point")} alias {d.Name}", d.SourceDdl!, d.TargetDdl ?? $"DROP ALIAS {Ddl.Q(lib, d.Name)}", d);
                    break;

                case (ObjectKind.UserProfile, _):
                {
                    var s = (UserProfileDef)d.Source!; var t = d.Target as UserProfileDef;
                    if (t is null)
                        Step(StepPhase.Deploy, 2, StepKind.Cl, $"Create profile {s.Name} (no password)", Ddl.Profile("CRTUSRPRF", s),
                            $"-- review, then: DLTUSRPRF USRPRF({s.Name}) OWNOBJOPT(*NODLT)", d);
                    else
                        Step(StepPhase.Deploy, 2, StepKind.Cl, $"Align profile {s.Name}", Ddl.Profile("CHGUSRPRF", s), Cl(Ddl.Profile("CHGUSRPRF", t)), d);
                    break;
                }
                case (ObjectKind.DataArea, ChangeKind.Added):
                {
                    var s = (DataAreaDef)d.Source!;
                    // A value that looks like a credential is never written into a script that is displayed and stored.
                    var secret = Masking.IsSensitiveName(s.Name) || (s.Value is not null && Masking.LooksLikeSecret(s.Value));
                    Step(StepPhase.Deploy, 12, StepKind.Cl, $"Create data area {d.Name}{(secret ? " (empty: value is sensitive)" : "")}",
                        Ddl.CreateDataArea(lib, s, secret ? "" : s.Value), Cl($"DLTDTAARA DTAARA({lib}/{d.Name})"), d);
                    if (secret) Step(StepPhase.PostCheck, 9, StepKind.Sql, $"Set {d.Name} by hand", $"-- {d.Name} holds a credential: the security team sets it with CHGDTAARA", null, d);
                    break;
                }
                case (ObjectKind.DataArea, ChangeKind.Modified):
                {
                    var s = (DataAreaDef)d.Source!; var t = (DataAreaDef)d.Target!;
                    Backup(lib, d.Name, "*DTAARA", false, restore: true);
                    Step(StepPhase.Deploy, 12, StepKind.Cl, $"Delete data area {d.Name} (re-created below)", $"DLTDTAARA DTAARA({lib}/{d.Name})", null, d);
                    var secret = Masking.IsSensitiveName(s.Name) || Masking.LooksLikeSecret(t.Value ?? "");
                    Step(StepPhase.Deploy, 12, StepKind.Cl, $"Re-create data area {d.Name} keeping the {cmp.Target.Key} value", Ddl.CreateDataArea(lib, s, secret ? "" : t.Value),
                        Cl($"DLTDTAARA DTAARA({lib}/{d.Name})"), d);
                    if (secret) Step(StepPhase.PostCheck, 9, StepKind.Sql, $"Restore {d.Name} by hand", $"-- {d.Name} holds a credential: re-enter it with CHGDTAARA (the backup copy in {backupLib} has it)", null, d);
                    break;
                }
                case (ObjectKind.DataArea, ChangeKind.Drift):
                {
                    var s = (DataAreaDef)d.Source!; var t = (DataAreaDef)d.Target!;
                    if (Masking.IsSensitiveName(s.Name) || Masking.LooksLikeSecret(s.Value ?? "") || Masking.LooksLikeSecret(t.Value ?? ""))
                        Step(StepPhase.PostCheck, 9, StepKind.Sql, $"Set {d.Name} by hand", $"-- {d.Name} holds a credential: values are never copied between environments", null, d);
                    else
                        Step(StepPhase.Deploy, 12, StepKind.Cl, $"Set value of {d.Name}", Ddl.ChangeDataArea(lib, t, s.Value), Cl(Ddl.ChangeDataArea(lib, t, t.Value)), d);
                    break;
                }
                case (ObjectKind.DataArea, ChangeKind.Removed):
                    Backup(lib, d.Name, "*DTAARA", false, restore: true);
                    Step(StepPhase.Deploy, 90, StepKind.Cl, $"Delete data area {d.Name}", $"DLTDTAARA DTAARA({lib}/{d.Name})", null, d, true);
                    break;

                case (ObjectKind.DataQueue, ChangeKind.Removed):
                    Step(StepPhase.Deploy, 90, StepKind.Cl, $"Delete data queue {d.Name}", $"DLTDTAQ DTAQ({lib}/{d.Name})", Cl(Ddl.CreateDataQueue(lib, (DataQueueDef)d.Target!)), d, true);
                    break;
                case (ObjectKind.DataQueue, _):
                {
                    var s = (DataQueueDef)d.Source!; var t = d.Target as DataQueueDef;
                    if (t is not null)
                        Step(StepPhase.Deploy, 13, StepKind.Cl, $"Delete data queue {d.Name} (re-created below)", $"DLTDTAQ DTAQ({lib}/{d.Name})", Cl(Ddl.CreateDataQueue(lib, t)), d, true);
                    Step(StepPhase.Deploy, 13, StepKind.Cl, $"Create data queue {d.Name}", Ddl.CreateDataQueue(lib, s), Cl($"DLTDTAQ DTAQ({lib}/{d.Name})"), d);
                    break;
                }

                case (ObjectKind.JobDescription, ChangeKind.Removed):
                    Step(StepPhase.Deploy, 90, StepKind.Cl, $"Delete job description {d.Name}", $"DLTJOBD JOBD({lib}/{d.Name})",
                        Cl(Ddl.JobDescription("CRTJOBD", lib, (JobDescriptionDef)d.Target!)), d, true);
                    break;
                case (ObjectKind.JobDescription, _):
                {
                    var s = (JobDescriptionDef)d.Source!; var t = d.Target as JobDescriptionDef;
                    Step(StepPhase.Deploy, 14, StepKind.Cl, $"{(t is null ? "Create" : "Align")} job description {d.Name}",
                        Ddl.JobDescription(t is null ? "CRTJOBD" : "CHGJOBD", lib, s),
                        Cl(t is null ? $"DLTJOBD JOBD({lib}/{d.Name})" : Ddl.JobDescription("CHGJOBD", lib, t)), d);
                    break;
                }

                case (ObjectKind.Message, _):
                {
                    var s = d.Source as MessageDef; var t = d.Target as MessageDef;
                    var file = d.Parent!;
                    if (s is not null && t is null && d.Findings.Any(f => f.Code == "MSG003") && _createdMsgf.Add($"{lib}/{file}"))
                        Step(StepPhase.Deploy, 15, StepKind.Cl, $"Create message file {file}", $"CRTMSGF MSGF({lib}/{file})", Cl($"DLTMSGF MSGF({lib}/{file})"), d);
                    if (s is null && t is not null)
                        Step(StepPhase.Deploy, 90, StepKind.Cl, $"Remove message {d.Name}", $"RMVMSGD MSGID({d.Name}) MSGF({lib}/{file})", Cl(Ddl.Message("ADDMSGD", lib, t)), d, true);
                    else if (t is null)
                        Step(StepPhase.Deploy, 16, StepKind.Cl, $"Add message {d.Name} to {file}", Ddl.Message("ADDMSGD", lib, s!), Cl($"RMVMSGD MSGID({d.Name}) MSGF({lib}/{file})"), d);
                    else
                        Step(StepPhase.Deploy, 16, StepKind.Cl, $"Change message {d.Name} in {file}", Ddl.Message("CHGMSGD", lib, s!), Cl(Ddl.Message("CHGMSGD", lib, t)), d);
                    break;
                }

                case (ObjectKind.Authority, _):
                {
                    var grants = (List<ObjectAuthorityDef>)d.Source!;
                    var current = (List<ObjectAuthorityDef>)d.Target!;
                    foreach (var g in grants)
                    {
                        var obj = $"{lib}/{SqlText.ClName(g.Object)}";
                        var now = current.FirstOrDefault(x => Eq(x.User, g.User))?.Authority;
                        if (g.User == "*OWNER")
                            Step(StepPhase.Security, 0, StepKind.Cl, $"Owner of {g.Object} → {g.Authority}", $"CHGOBJOWN OBJ({obj}) OBJTYPE({g.ObjectType}) NEWOWN({SqlText.ClName(g.Authority)}) CUROWNAUT(*SAME)",
                                now is null ? null : Cl($"CHGOBJOWN OBJ({obj}) OBJTYPE({g.ObjectType}) NEWOWN({now}) CUROWNAUT(*SAME)"), d);
                        else if (g.Authority.StartsWith('*'))
                            Step(StepPhase.Security, 1, StepKind.Cl, $"{g.User} {g.Authority} on {g.Object}", $"GRTOBJAUT OBJ({obj}) OBJTYPE({g.ObjectType}) USER({SqlText.ClName(g.User)}) AUT({g.Authority}) REPLACE(*YES)",
                                Cl(now is not null && now.StartsWith('*') ? $"GRTOBJAUT OBJ({obj}) OBJTYPE({g.ObjectType}) USER({g.User}) AUT({now}) REPLACE(*YES)" : $"RVKOBJAUT OBJ({obj}) OBJTYPE({g.ObjectType}) USER({g.User}) AUT(*ALL)"), d);
                    }
                    break;
                }

                case (ObjectKind.BindingDirectory, _):
                    EmitBindingDirectory(d);
                    break;
                case (ObjectKind.AuthorizationList, _):
                    EmitAuthorizationList(d);
                    break;
                case (ObjectKind.JobSchedule, _):
                    EmitJobSchedule(d);
                    break;
                case (ObjectKind.SystemValue, _):
                {
                    var s = (SystemValueDef)d.Source!; var t = (SystemValueDef)d.Target!;
                    string? Value(SystemValueDef v) => v.Numeric
                        ? (Regex.IsMatch(v.Value, @"^-?\d+(\.\d+)?$") ? v.Value : null)
                        : SqlText.ClText(v.Value);
                    if (Value(s) is { } nv && Value(t) is { } ov && TurnoverOptions.IsValidLibraryName(s.Name))
                        Step(StepPhase.Security, 6, StepKind.Cl, $"System value {s.Name} → {s.Value}", $"CHGSYSVAL SYSVAL({s.Name}) VALUE({nv})", Cl($"CHGSYSVAL SYSVAL({s.Name}) VALUE({ov})"), d);
                    else
                        Step(StepPhase.Security, 6, StepKind.Sql, $"Set {s.Name} by hand", $"-- set system value {s.Name} to {s.Value} with CHGSYSVAL (value not in a form the tool writes)", null, d);
                    break;
                }
                case (ObjectKind.IfsFile, _):
                    EmitIfs(d);
                    break;

                case (ObjectKind.DdsFile or ObjectKind.Command or ObjectKind.Menu, ChangeKind.Added or ChangeKind.Modified):
                case (ObjectKind.Program or ObjectKind.ServiceProgram, ChangeKind.Added or ChangeKind.Modified):
                    EmitProgram(d, (ProgramObject)d.Source!, (ProgramObject?)d.Target, copySource: true);
                    break;
                case (ObjectKind.Program or ObjectKind.ServiceProgram, ChangeKind.Drift):
                    EmitProgram(d, (ProgramObject)d.Target!, (ProgramObject)d.Target!, copySource: false);
                    break;

                case (ObjectKind.Function or ObjectKind.Procedure, ChangeKind.Added or ChangeKind.Modified):
                {
                    var s = (RoutineDef)d.Source!; var t = (RoutineDef?)d.Target;
                    var rank = d.Kind == ObjectKind.Function ? 70 : 74;
                    var word = d.Kind == ObjectKind.Function ? "FUNCTION" : "PROCEDURE";
                    if (t is not null && !Eq(t.SpecificName, s.SpecificName))
                        Step(StepPhase.Routines, rank, StepKind.Sql, $"Drop old signature {t.SpecificName}", $"DROP SPECIFIC {word} {Ddl.Q(lib, t.SpecificName)}", t.Definition, d, true);
                    Step(StepPhase.Routines, rank + 1, StepKind.Sql, $"{(t is null ? "Create" : "Replace")} {word.ToLowerInvariant()} {d.Name}", d.SourceDdl!,
                        t is not null && Eq(t.SpecificName, s.SpecificName) ? t.Definition : $"DROP SPECIFIC {word} {Ddl.Q(lib, s.SpecificName)}", d);
                    break;
                }
                case (ObjectKind.Function or ObjectKind.Procedure, ChangeKind.Removed):
                {
                    var t = (RoutineDef)d.Target!;
                    Step(StepPhase.Routines, 69, StepKind.Sql, $"Drop {t.Kind.ToLowerInvariant()} {t.SpecificName}", $"DROP SPECIFIC {t.Kind} {Ddl.Q(lib, t.SpecificName)}", t.Definition, d, true);
                    break;
                }

                case (ObjectKind.View, ChangeKind.Removed):
                    Step(StepPhase.Routines, 68, StepKind.Sql, $"Drop view {d.Name}", $"DROP VIEW {Ddl.Q(lib, d.Name)} RESTRICT", d.TargetDdl, d, true);   // RESTRICT: never cascade to views built on it
                    break;
                case (ObjectKind.Trigger, ChangeKind.Removed):
                    Step(StepPhase.Routines, 67, StepKind.Sql, $"Drop trigger {d.Name}", $"DROP TRIGGER {Ddl.Q(lib, d.Name)}", d.TargetDdl, d, true);
                    break;
                case (ObjectKind.View, _):
                    Step(StepPhase.Routines, 72, StepKind.Sql, $"{(d.Target is null ? "Create" : "Re-create")} view {d.Name}", d.SourceDdl!,
                        d.TargetDdl ?? $"DROP VIEW {Ddl.Q(lib, d.Name)}", d);
                    break;

                case (ObjectKind.Trigger, _):
                    Step(StepPhase.Routines, 76, StepKind.Sql, $"{(d.Target is null ? "Create" : "Replace")} trigger {d.Name}", d.SourceDdl!,
                        d.TargetDdl ?? $"DROP TRIGGER {Ddl.Q(lib, d.Name)}", d);
                    break;
            }

            // Re-binds (UPDPGM) run after compiles; re-grants run with the other authority steps.
            foreach (var cl in d.FollowUps)
                if (cl.StartsWith("UPDPGM", StringComparison.Ordinal))
                    Step(StepPhase.Compile, 66, StepKind.Cl, $"Re-bind {cl.Split(' ')[1]}", cl, null, d);
                else
                    Step(StepPhase.Security, 2, StepKind.Cl, $"Re-apply {cl.Split(' ')[0]} on {d.Name}", cl, null, d);
        }

        // ---------------------------------------------------------------- locks

        private readonly HashSet<string> _lockChecked = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// ALTER TABLE, CHGPF, DLTDTAARA and DLTDTAQ need the object to themselves. A job that holds it open makes the
        /// statement wait and then fail half-way; checking first stops the deployment before anything has changed.
        /// </summary>
        private void LockCheck(string lib, string? obj, string type, Difference d)
        {
            if (string.IsNullOrEmpty(obj) || string.IsNullOrEmpty(lib) || !_lockChecked.Add($"{lib}/{obj}/{type}")) return;
            _steps.Add(new PlanStep
            {
                Phase = StepPhase.PreCheck, Rank = 1, Kind = StepKind.Check, DiffId = d.Id, Title = $"No job holds {lib}/{obj} {type}",
                Command = $"SELECT COUNT(*) FROM QSYS2.OBJECT_LOCK_INFO WHERE SYSTEM_OBJECT_SCHEMA = {SqlText.Literal(lib)} " +
                          $"AND (SYSTEM_OBJECT_NAME = {SqlText.Literal(obj.ToUpperInvariant())} OR OBJECT_NAME = {SqlText.Literal(obj.ToUpperInvariant())}) AND OBJECT_TYPE = {SqlText.Literal(type)}",
            });
        }

        private void Locks(Difference d)
        {
            var lib = d.TargetLibrary;
            switch (d.Kind, d.Change)
            {
                case (ObjectKind.Column, _):
                case (ObjectKind.CheckConstraint, _):
                    LockCheck(lib, d.TableSystemName ?? d.Parent, "*FILE", d);
                    break;
                case (ObjectKind.Table, ChangeKind.Modified or ChangeKind.Removed):
                    LockCheck(lib, ((TableDef)d.Target!).SystemName, "*FILE", d);
                    break;
                case (ObjectKind.ForeignKey, ChangeKind.Added or ChangeKind.Modified or ChangeKind.Removed):
                    LockCheck(lib, ((ForeignKeyDef)(d.Target ?? d.Source)!).Table, "*FILE", d);
                    break;
                case (ObjectKind.Index, ChangeKind.Modified or ChangeKind.Removed):
                    LockCheck(lib, d.Name, "*FILE", d);
                    break;
                case (ObjectKind.DataArea, ChangeKind.Modified or ChangeKind.Removed):
                    LockCheck(lib, d.Name, "*DTAARA", d);
                    break;
                case (ObjectKind.DataQueue, ChangeKind.Modified or ChangeKind.Removed):
                    LockCheck(lib, d.Name, "*DTAQ", d);
                    break;
                case (ObjectKind.DdsFile, ChangeKind.Modified):
                    LockCheck(lib, d.Name, "*FILE", d);
                    break;
            }
        }

        // ---------------------------------------------------------------- custom steps

        public void Custom(CustomStep cs)
        {
            var (phase, rank) = cs.Point switch
            {
                CustomStepPoint.BeforeChanges => (StepPhase.Backup, 1000),
                CustomStepPoint.AfterTables => (StepPhase.Deploy, 1000),
                _ => (StepPhase.Security, 1000),
            };
            var undo = string.IsNullOrWhiteSpace(cs.Rollback) ? null : cs.Kind == StepKind.Cl && !DeploymentPlanner.IsManual(cs.Rollback) ? Cl(cs.Rollback.Trim()) : cs.Rollback.Trim();
            _steps.Add(new PlanStep
            {
                Phase = phase, Rank = rank, Kind = cs.Kind, Title = $"Custom: {cs.Title}", Command = cs.Command.Trim().TrimEnd(';'), Rollback = undo, DiffId = "CUSTOM:" + cs.Id,
            });
        }

        // ---------------------------------------------------------------- binding directories, authorization lists, schedules, IFS

        private static string Entry(BindingEntryDef e, bool withActivation) =>
            $"({(e.Library.StartsWith('*') ? e.Library : SqlText.ClName(e.Library))}/{SqlText.ClName(e.Object)} {e.Type}{(withActivation && Eq(e.Type, "*SRVPGM") ? " " + e.Activation : "")})";

        private void EmitBindingDirectory(Difference d)
        {
            var lib = d.TargetLibrary;
            var bnd = $"{lib}/{SqlText.ClName(d.Name)}";
            var s = d.Source as BindingDirectoryDef; var t = d.Target as BindingDirectoryDef;
            if (s is null)
            {
                Step(StepPhase.Deploy, 90, StepKind.Cl, $"Delete binding directory {d.Name}", $"DLTBNDDIR BNDDIR({bnd})",
                    $"-- re-create {bnd}: CRTBNDDIR, then ADDBNDDIRE {string.Join(" ", t!.Entries.Select(e => Entry(e, true)))}", d, true);
                return;
            }
            if (t is null)
                Step(StepPhase.Deploy, 17, StepKind.Cl, $"Create binding directory {d.Name}", $"CRTBNDDIR BNDDIR({bnd}) TEXT({SqlText.ClText(s.Text)})", Cl($"DLTBNDDIR BNDDIR({bnd})"), d);
            else if (!Eq(s.Text, t.Text))
                Step(StepPhase.Deploy, 17, StepKind.Cl, $"Text of {d.Name}", $"CHGOBJD OBJ({bnd}) OBJTYPE(*BNDDIR) TEXT({SqlText.ClText(s.Text)})", Cl($"CHGOBJD OBJ({bnd}) OBJTYPE(*BNDDIR) TEXT({SqlText.ClText(t.Text)})"), d);
            var have = t?.Entries ?? [];
            foreach (var e in have.Where(x => !s.Entries.Any(y => y.ToString() == x.ToString())))
                Step(StepPhase.Deploy, 17, StepKind.Cl, $"Remove {e.Object} from {d.Name}", $"RMVBNDDIRE BNDDIR({bnd}) OBJ({Entry(e, false)})", Cl($"ADDBNDDIRE BNDDIR({bnd}) OBJ({Entry(e, true)})"), d);
            foreach (var e in s.Entries.Where(x => !have.Any(y => y.ToString() == x.ToString())))
                Step(StepPhase.Deploy, 17, StepKind.Cl, $"Add {e.Object} to {d.Name}", $"ADDBNDDIRE BNDDIR({bnd}) OBJ({Entry(e, true)})", Cl($"RMVBNDDIRE BNDDIR({bnd}) OBJ({Entry(e, false)})"), d);
        }

        private void EmitAuthorizationList(Difference d)
        {
            var s = d.Source as AuthorizationListDef; var t = d.Target as AuthorizationListDef;
            if (s is null)
            {
                // An authorization list secures objects: deleting it opens them up. Listed for review only.
                Step(StepPhase.Security, 3, StepKind.Sql, $"Review authorization list {d.Name}", $"-- {d.Name} exists only in {cmp.Target.Key}: check what it secures (DSPAUTLOBJ) before deleting it by hand", null, d);
                return;
            }
            var autl = SqlText.ClName(s.Name);
            var have = t?.Entries ?? [];
            if (t is null)
            {
                var pub = s.Entries.FirstOrDefault(e => e.User == "*PUBLIC")?.Authority ?? "*EXCLUDE";
                Step(StepPhase.Deploy, 3, StepKind.Cl, $"Create authorization list {s.Name}", $"CRTAUTL AUTL({autl}) AUT({(pub.StartsWith('*') ? pub : "*EXCLUDE")})", Cl($"DLTAUTL AUTL({autl})"), d);
                have = [new AuthorizationListEntry { User = "*PUBLIC", Authority = pub }];
            }
            foreach (var e in s.Entries)
            {
                var now = have.FirstOrDefault(x => Eq(x.User, e.User));
                if (now is not null && Eq(now.Authority, e.Authority)) continue;
                if (!e.Authority.StartsWith('*'))
                {
                    Step(StepPhase.Security, 3, StepKind.Sql, $"{e.User} on {s.Name} by hand", $"-- give {e.User} user-defined authority {e.Authority} on {s.Name} with EDTAUTL", null, d);
                    continue;
                }
                var user = e.User == "*PUBLIC" ? "*PUBLIC" : SqlText.ClName(e.User);
                if (now is null)
                    Step(StepPhase.Security, 3, StepKind.Cl, $"{e.User} {e.Authority} on {s.Name}", $"ADDAUTLE AUTL({autl}) USER({user}) AUT({e.Authority})", Cl($"RMVAUTLE AUTL({autl}) USER({user})"), d);
                else
                    Step(StepPhase.Security, 3, StepKind.Cl, $"{e.User} {now.Authority} → {e.Authority} on {s.Name}", $"CHGAUTLE AUTL({autl}) USER({user}) AUT({e.Authority})",
                        now.Authority.StartsWith('*') ? Cl($"CHGAUTLE AUTL({autl}) USER({user}) AUT({now.Authority})") : null, d);
            }
            foreach (var e in have.Where(x => x.User != "*PUBLIC" && !s.Entries.Any(y => Eq(y.User, x.User))))
                Step(StepPhase.Security, 3, StepKind.Cl, $"Remove {e.User} from {s.Name}", $"RMVAUTLE AUTL({autl}) USER({SqlText.ClName(e.User)})",
                    e.Authority.StartsWith('*') ? Cl($"ADDAUTLE AUTL({autl}) USER({SqlText.ClName(e.User)}) AUT({e.Authority})") : null, d, true);
        }

        private static string? ScheduleParams(JobScheduleDef j)
        {
            static string Time(string t) => new string(t.Where(char.IsDigit).ToArray()).PadRight(6, '0')[..6];
            var specialDate = j.ScheduledDate.StartsWith('*');
            if (!specialDate || !Regex.IsMatch(j.ScheduledDays, @"^(\*[A-Z]+)( \*[A-Z]+)*$") || !Regex.IsMatch(j.Frequency, @"^\*[A-Z]+$")) return null;
            var rel = Eq(j.Frequency, "*MONTHLY") && Regex.IsMatch(j.RelativeDays, @"^[1-5*A-Z]+( [1-5*A-Z]+)*$") ? $" RELDAYMON({j.RelativeDays})" : "";
            return $"CMD({SqlText.ClText(j.Command)}) FRQ({j.Frequency}) SCDDATE({j.ScheduledDate}) SCDDAY({j.ScheduledDays}) SCDTIME({Time(j.ScheduledTime)}){rel} " +
                   $"JOBD({(j.JobDescription.StartsWith('*') ? j.JobDescription : Ddl.QualifiedCl(j.JobDescription))}) " +
                   $"JOBQ({(j.JobQueue.StartsWith('*') ? j.JobQueue : Ddl.QualifiedCl(j.JobQueue))}) USER({(j.User.StartsWith('*') ? j.User : SqlText.ClName(j.User))}) TEXT({SqlText.ClText(j.Text)})";
        }

        private void EmitJobSchedule(Difference d)
        {
            var s = d.Source as JobScheduleDef; var t = d.Target as JobScheduleDef;
            var job = SqlText.ClName(d.Name);
            if (s is null)
            {
                Step(StepPhase.Security, 5, StepKind.Sql, $"Review schedule entry {d.Name}", $"-- {d.Name} is scheduled only in {cmp.Target.Key}: remove it by hand (RMVJOBSCDE JOB({job}) ENTRYNBR(*ONLY)) if it is obsolete", null, d);
                return;
            }
            var want = ScheduleParams(s);
            var old = t is null ? null : ScheduleParams(t);
            if (want is null || (t is not null && old is null))
            {
                Step(StepPhase.Security, 5, StepKind.Sql, $"Schedule {d.Name} by hand", $"-- {d.Name}: schedule '{s.Frequency} {s.ScheduledDate} {s.ScheduledDays} {s.ScheduledTime}' must be entered with WRKJOBSCDE", null, d);
                return;
            }
            if (t is null)
            {
                Step(StepPhase.Security, 5, StepKind.Cl, $"Add schedule entry {d.Name}", $"ADDJOBSCDE JOB({job}) {want}", Cl($"RMVJOBSCDE JOB({job}) ENTRYNBR(*ONLY)"), d);
                if (Eq(s.Status, "HELD")) Step(StepPhase.Security, 5, StepKind.Cl, $"Hold schedule entry {d.Name}", $"HLDJOBSCDE JOB({job}) ENTRYNBR(*ONLY)", null, d);
            }
            else
                Step(StepPhase.Security, 5, StepKind.Cl, $"Change schedule entry {d.Name}", $"CHGJOBSCDE JOB({job}) ENTRYNBR(*ONLY) {want}", Cl($"CHGJOBSCDE JOB({job}) ENTRYNBR(*ONLY) {old}"), d);
        }

        private void EmitIfs(Difference d)
        {
            var src = $"{d.SourceLibrary.TrimEnd('/')}/{d.Name}";
            var dst = $"{d.TargetLibrary.TrimEnd('/')}/{d.Name}";
            var backup = $"{dst}.{backupLib}";
            if (!TurnoverOptions.IsSafeIfsPath(src) || !TurnoverOptions.IsSafeIfsPath(dst))
            {
                Step(StepPhase.Deploy, 85, StepKind.Sql, $"Copy {d.Name} by hand", $"-- the path {d.Name} contains characters the tool will not put in a command; copy it by hand", null, d);
                return;
            }
            string Q(string path) => SqlText.ClText(path);
            var existed = d.Target is not null;
            if (existed)
                Step(StepPhase.Backup, 0, StepKind.Cl, $"Back up {dst}", $"CPY OBJ({Q(dst)}) TOOBJ({Q(backup)}) REPLACE(*YES)", null, d);
            var undo = existed ? Cl($"CPY OBJ({Q(backup)}) TOOBJ({Q(dst)}) REPLACE(*YES)") : Cl($"RMVLNK OBJLNK({Q(dst)})");
            if (d.Source is null)
            {
                Step(StepPhase.Deploy, 85, StepKind.Cl, $"Remove {dst}", $"RMVLNK OBJLNK({Q(dst)})", undo, d, true);
                return;
            }
            var stamp = d.Source is IfsFileDef sf ? (sf.Hash is { } h ? $" (sha {h})" : $" ({sf.Size} bytes)") : "";
            if (SameHostEverywhere)
                Step(StepPhase.Deploy, 85, StepKind.Cl, $"Copy {d.Name} to {d.TargetLibrary}{stamp}", $"CPY OBJ({Q(src)}) TOOBJ({Q(dst)}) REPLACE(*YES)", undo, d);
            else
                _steps.Add(new PlanStep
                {
                    Phase = StepPhase.Deploy, Rank = 85, Kind = StepKind.CopyIfs, DiffId = d.Id, Rollback = undo,
                    Title = $"Copy {d.Name} from {cmp.Source.Key} to {d.TargetLibrary}{stamp}", SourceEnvironment = cmp.Source.Key,
                    CopyFrom = src, CopyTo = dst, Ccsid = (d.Source as IfsFileDef)?.Ccsid is > 0 and var cc ? cc : 1208,
                    Command = $"-- copy stream file {src} on {cmp.Source.Host} to {dst} on {cmp.Target.Host} (done by the Turnover deploy engine with IFS_READ_BINARY / IFS_WRITE_BINARY)",
                });
        }

        private void EmitColumn(Difference d)
        {
            var lib = d.TargetLibrary;
            var table = d.Parent!;
            var q = Ddl.Q(lib, table);
            var sys = d.TableSystemName;

            switch (d.Change)
            {
                case ChangeKind.Added:
                {
                    var c = (ColumnDef)d.Source!;
                    if (sys is not null) Backup(lib, sys, "*FILE", true);
                    Step(StepPhase.Deploy, 30, StepKind.Sql, $"Add column {table}.{c.Name}", $"ALTER TABLE {q} ADD COLUMN {Ddl.ColumnSpec(c)}",
                        $"ALTER TABLE {q} DROP COLUMN {Ddl.C(c.Name)} RESTRICT", d);
                    break;
                }
                case ChangeKind.Renamed:
                {
                    var c = (ColumnDef)d.Source!; var old = (ColumnDef)d.Target!;
                    if (sys is not null) Backup(lib, sys, "*FILE", true);
                    // Add the new column nullable so existing rows are accepted, copy, then tighten.
                    var spec = Ddl.ColumnSpec(new ColumnDef { Name = c.Name, SystemName = c.SystemName, DataType = c.DataType, Length = c.Length, Scale = c.Scale, Ccsid = c.Ccsid, Nullable = true, Default = c.Default });
                    Step(StepPhase.Deploy, 31, StepKind.Sql, $"Rename {table}.{old.Name} → {c.Name} (1/4: add)", $"ALTER TABLE {q} ADD COLUMN {spec}", $"ALTER TABLE {q} DROP COLUMN {Ddl.C(c.Name)} RESTRICT", d);
                    Step(StepPhase.Deploy, 31, StepKind.Sql, $"Rename {table}.{old.Name} → {c.Name} (2/4: copy)", $"UPDATE {q} SET {Ddl.C(c.Name)} = {Ddl.C(old.Name)} WITH NC", null, d);
                    if (!c.Nullable)
                        Step(StepPhase.Deploy, 31, StepKind.Sql, $"Rename {table}.{old.Name} → {c.Name} (3/4: NOT NULL)", $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(c.Name)} SET NOT NULL", $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(c.Name)} DROP NOT NULL", d);
                    Step(StepPhase.Deploy, 31, StepKind.Sql, $"Rename {table}.{old.Name} → {c.Name} (4/4: drop old)", $"ALTER TABLE {q} DROP COLUMN {Ddl.C(old.Name)} RESTRICT",
                        $"-- restore {old.Name} from {backupLib}: ALTER TABLE {q} ADD COLUMN {Ddl.ColumnSpec(old)} then UPDATE from the backup copy", d, true);
                    break;
                }
                case ChangeKind.Removed:
                {
                    var c = (ColumnDef)d.Target!;
                    if (sys is not null) Backup(lib, sys, "*FILE", true);
                    Step(StepPhase.Deploy, 33, StepKind.Sql, $"Drop column {table}.{c.Name}", $"ALTER TABLE {q} DROP COLUMN {Ddl.C(c.Name)} RESTRICT",
                        $"-- restore {c.Name} from {backupLib}: ALTER TABLE {q} ADD COLUMN {Ddl.ColumnSpec(c)} then UPDATE from the backup copy", d, true);
                    break;
                }
                default:
                {
                    var s = (ColumnDef)d.Source!; var t = (ColumnDef)d.Target!;
                    var structural = s.TypeSignature != t.TypeSignature || s.Nullable != t.Nullable || s.Ccsid != t.Ccsid || !Eq(s.Default, t.Default);
                    if (structural && sys is not null) Backup(lib, sys, "*FILE", true);
                    if (s.TypeSignature != t.TypeSignature || s.Ccsid != t.Ccsid)
                        Step(StepPhase.Deploy, 32, StepKind.Sql, $"Change type of {table}.{s.Name} to {s.TypeSignature}",
                            $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} SET DATA TYPE {s.TypeSignature}{(s.Ccsid is null ? "" : " CCSID " + s.Ccsid)}",
                            $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} SET DATA TYPE {t.TypeSignature}{(t.Ccsid is null ? "" : " CCSID " + t.Ccsid)}", d,
                            destructive: d.Findings.Any(f => f.Code == "COL003" && f.Severity == Severity.Blocker));
                    if (!Eq(s.Default, t.Default))
                        Step(StepPhase.Deploy, 32, StepKind.Sql, $"Default of {table}.{s.Name}",
                            s.Default is null ? $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} DROP DEFAULT" : $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} SET DEFAULT {s.Default}",
                            t.Default is null ? $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} DROP DEFAULT" : $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} SET DEFAULT {t.Default}", d);
                    if (s.Nullable != t.Nullable)
                        Step(StepPhase.Deploy, 32, StepKind.Sql, $"{(s.Nullable ? "Allow" : "Disallow")} NULL in {table}.{s.Name}",
                            $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} {(s.Nullable ? "DROP NOT NULL" : "SET NOT NULL")}",
                            $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} {(t.Nullable ? "DROP NOT NULL" : "SET NOT NULL")}", d);
                    if (!Eq(s.Text, t.Text))
                        Step(StepPhase.Deploy, 35, StepKind.Sql, $"Text of {table}.{s.Name}", Ddl.Label($"COLUMN {q}.{Ddl.C(s.Name)}", s.Text, columnText: true),
                            Ddl.Label($"COLUMN {q}.{Ddl.C(s.Name)}", t.Text, columnText: true), d);
                    if (!Eq(s.Heading, t.Heading))
                        Step(StepPhase.Deploy, 35, StepKind.Sql, $"Heading of {table}.{s.Name}", Ddl.Label($"COLUMN {q}.{Ddl.C(s.Name)}", s.Heading),
                            Ddl.Label($"COLUMN {q}.{Ddl.C(s.Name)}", t.Heading), d);
                    if (d.Findings.Any(f => f.Code == "COL009") && d.RestartWith is long restart)
                        Step(StepPhase.Deploy, 34, StepKind.Sql, $"Move identity {table}.{s.Name} past existing keys",
                            $"ALTER TABLE {q} ALTER COLUMN {Ddl.C(s.Name)} RESTART WITH {restart}", null, d);
                    break;
                }
            }
        }

        private void EmitProgram(Difference d, ProgramObject src, ProgramObject? existing, bool copySource)
        {
            _needsLibl = true;
            var levelCheckOnly = !copySource;
            var pgmLib = d.TargetLibrary;
            var map = new LibraryMap(cmp.Pairs, [], cmp.Source, cmp.Target);
            var tgtSrcLib = !copySource ? src.SourceLibrary : cmp.SourceFileLibrary ?? map.MapLibrary(src.SourceLibrary);
            var member = string.IsNullOrEmpty(src.SourceMember) ? src.Name : src.SourceMember;
            // Files before the programs that use them; service programs before programs; commands after their program.
            var rank = (src.ObjectType.ToUpperInvariant(), src.Attribute.ToUpperInvariant()) switch
            {
                ("*FILE", "PF") => 56, ("*FILE", "LF") => 57, ("*FILE", _) => 58, ("*SRVPGM", _) => 60, ("*CMD" or "*MENU" or "*PNLGRP", _) => 64, _ => 62,
            };
            var ddsPhysical = Eq(src.ObjectType, "*FILE") && Eq(src.Attribute, "PF");

            // Undo order is the reverse of this: delete the new object first, then put the backup copy back.
            // A DDS physical file keeps its data through CHGPF, so its backup includes the data and its undo is manual.
            if (existing is not null) Backup(pgmLib, existing.Name, existing.ObjectType, ddsPhysical, restore: !ddsPhysical);
            // Library-to-library inside one environment shares the source file: nothing to copy.
            if (copySource && Eq(tgtSrcLib, src.SourceLibrary)) copySource = false;
            if (copySource)
            {
                CopyMember(d, rank, src.SourceLibrary, src.SourceFile, member, tgtSrcLib, existing is not null, SourceStamp(src));
                // A service program's binder source decides its signature: it travels with the program source.
                if (src.Options is { ExportSourceLibrary: { Length: > 0 } expLib, ExportSourceFile: { Length: > 0 } expFile } && !Eq(map.MapLibrary(expLib), expLib))
                    CopyMember(d, rank, expLib, expFile, src.Options.ExportSourceMember ?? src.Name, map.MapLibrary(expLib), existing is not null, null);
            }
            var cmdPgm = src.CommandProgram is null ? null : map.MapQualifiedObject(src.CommandProgram);
            // Replays the recorded compile/bind options. A level-check recompile rebuilds the target's own object, so
            // its bindings are already target names; a promotion maps them from the source environment.
            Func<string, string> mapObj = levelCheckOnly ? x => x : map.MapQualifiedObject;
            Func<string, string> mapLib = levelCheckOnly ? x => x : map.MapLibrary;
            var build = string.IsNullOrEmpty(src.SourceLibrary) ? null
                : Builds.Commands(src, pgmLib, tgtSrcLib, existing is not null, mapObj, mapLib, cmdPgm);
            if (build is null)
            {
                // Multi-module objects, objects without recorded source, and languages without a known build.
                Step(StepPhase.Compile, rank, StepKind.Sql, $"Build {src.Name} by hand",
                    $"-- {pgmLib}/{src.Name} ({src.ObjectType} {src.Attribute}{(src.ModuleCount > 1 ? $", {src.ModuleCount} modules" : "")}) cannot be rebuilt by the tool: build it with your normal process", null, d);
                return;
            }
            var cmds = build;            var undo = ddsPhysical && existing is not null
                ? $"-- {src.Name} was changed in place with CHGPF; restore it from {backupLib}/{existing.Name} (with data) only after checking what changed"
                : Cl($"DLTOBJ OBJ({pgmLib}/{src.Name}) OBJTYPE({src.ObjectType})");
            for (var i = 0; i < cmds.Count; i++)
                Step(StepPhase.Compile, rank, StepKind.Cl,
                    (!levelCheckOnly ? $"Build {src.Name} ({src.Attribute}, {SourceStamp(src)})" : $"Recompile {src.Name} for new record format (level check)") + (cmds.Count > 1 ? $" {i + 1}/{cmds.Count}" : ""),
                    cmds[i], i == cmds.Count - 1 ? undo : null, d);
            _steps.Add(new PlanStep
            {
                Phase = StepPhase.PostCheck, Rank = 0, Kind = StepKind.Check, DiffId = d.Id, Title = $"{pgmLib}/{src.Name} exists",
                Command = $"SELECT 1 - COUNT(*) FROM TABLE(QSYS2.OBJECT_STATISTICS({SqlText.Literal(pgmLib)}, {SqlText.Literal(src.ObjectType)}, {SqlText.Literal(src.Name)})) X",
            });
        }

        /// <summary>
        /// Copies one source member into the target source library: CPYF when both sides are on the same partition,
        /// otherwise a deploy-engine copy over two connections (CPYF on the target cannot see another system's file).
        /// </summary>
        /// <summary>
        /// What the approved source looked like (content hash, or the change date when hashing is off). It goes into the
        /// step title, so it is part of the script hash: source edited after approval no longer matches and deploy refuses.
        /// </summary>
        private static string SourceStamp(ProgramObject p) =>
            p.SourceHash is { Length: > 0 } h ? $"source {h}" : $"source changed {p.SourceChanged:yyyy-MM-dd HH:mm:ss}";

        /// <summary>Same partition as the source for the target AND every Deploy replica: only then can CPYF / CPY see the source.</summary>
        private bool SameHostEverywhere => Eq(cmp.Source.Host, cmp.Target.Host) && !cmp.Target.Distribution.Any(x => x.Mode == DistributionMode.Deploy);

        private void CopyMember(Difference d, int rank, string fromLib, string file, string member, string toLib, bool targetHadIt, string? stamp)
        {
            var suffix = stamp is null ? "" : $" ({stamp})";
            if (targetHadIt)
                Step(StepPhase.Backup, 0, StepKind.Cl, $"Back up source {toLib}/{file}({member})",
                    $"CPYF FROMFILE({toLib}/{file}) TOFILE({backupLib}/{file}) FROMMBR({member}) TOMBR({member}) MBROPT(*REPLACE) CRTFILE(*YES)", null, d);
            var undoCopy = Cl(targetHadIt
                ? $"CPYF FROMFILE({backupLib}/{file}) TOFILE({toLib}/{file}) FROMMBR({member}) TOMBR({member}) MBROPT(*REPLACE)"
                : $"RMVM FILE({toLib}/{file}) MBR({member})");
            if (SameHostEverywhere)
                Step(StepPhase.Compile, rank, StepKind.Cl, $"Copy source {member} to {toLib}/{file}{suffix}",
                    $"CPYF FROMFILE({fromLib}/{file}) TOFILE({toLib}/{file}) FROMMBR({member}) TOMBR({member}) MBROPT(*REPLACE)", undoCopy, d);
            else
                _steps.Add(new PlanStep
                {
                    Phase = StepPhase.Compile, Rank = rank, Kind = StepKind.CopySource, DiffId = d.Id, Rollback = undoCopy,
                    Title = $"Copy source {member} from {cmp.Source.Key} to {toLib}/{file}{suffix}",
                    SourceEnvironment = cmp.Source.Key,
                    CopyFrom = $"{fromLib}/{file}({member})", CopyTo = $"{toLib}/{file}({member})",
                    Command = $"-- copy member {fromLib}/{file}({member}) on {cmp.Source.Host} to {toLib}/{file}({member}) on {cmp.Target.Host} (done by the Turnover deploy engine; by hand: SAVOBJ/RSTOBJ or a DDM file)",
                });
        }

        private void PostExists(Difference d, string catalog, string schemaCol, string nameCol) =>
            _steps.Add(new PlanStep
            {
                Phase = StepPhase.PostCheck, Rank = 0, Kind = StepKind.Check, DiffId = d.Id, Title = $"{d.TargetLibrary}.{d.Name} exists",
                Command = $"SELECT 1 - COUNT(*) FROM QSYS2.{catalog} WHERE {schemaCol} = {SqlText.Literal(d.TargetLibrary)} AND {nameCol} = {SqlText.Literal(d.Name.ToUpperInvariant())}",
            });

        private string BackupName(string lib, string obj, string type) =>
            _backedUp.TryGetValue($"{lib}/{obj}/{type}", out var n) ? n : obj;

        private void Backup(string lib, string obj, string type, bool data, bool restore = false)
        {
            var key = $"{lib}/{obj}/{type}";
            if (_backedUp.ContainsKey(key)) return;
            var name = obj;
            for (var i = 1; !_backupNames.Add(name); i++) name = obj[..Math.Min(obj.Length, 7)] + i.ToString("D3");
            _backedUp[key] = name;
            Step(StepPhase.Backup, 0, StepKind.Cl, $"Back up {lib}/{obj} {type}{(data ? " with data" : "")}",
                $"CRTDUPOBJ OBJ({obj}) FROMLIB({lib}) OBJTYPE({type}) TOLIB({backupLib}){(Eq(name, obj) ? "" : $" NEWOBJ({name})")}{(data ? " DATA(*YES) CST(*NO) TRG(*NO)" : "")}",
                restore ? Cl($"CRTDUPOBJ OBJ({name}) FROMLIB({backupLib}) OBJTYPE({type}) TOLIB({lib}) NEWOBJ({obj})") : null);
        }

        public List<PlanStep> Finish(List<Difference> diffs)
        {
            if (_steps.Count == 0) return _steps;
            var hasBackup = _steps.Any(s => s.Phase == StepPhase.Backup);
            if (hasBackup)
                Step(StepPhase.Setup, 0, StepKind.Cl, $"Create backup library {backupLib}",
                    $"CRTLIB LIB({backupLib}) TYPE(*TEST) TEXT('{packageCode} backup {cmp.Source.Key}->{cmp.Target.Key}')");
            if (_needsLibl)
            {
                // Compiles resolve *LIBL files and /COPY members through the job library list: point it at the target.
                // Only libraries that exist (or that this plan creates first): CHGLIBL fails with CPF2110 on a missing one.
                var created = diffs.Where(x => x.Kind == ObjectKind.Library).Select(x => x.TargetLibrary);
                var libl = string.Join(" ", cmp.Target.Libraries.Values
                    .Where(l => cmp.TargetLibrariesPresent.Count == 0 || cmp.TargetLibrariesPresent.Contains(l, StringComparer.OrdinalIgnoreCase) || created.Contains(l, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(250));
                Step(StepPhase.Setup, 10, StepKind.Cl, $"Library list → {cmp.Target.Key}", $"CHGLIBL LIBL({libl})");
            }

            // Environment wrap-around (Turnover:Environments:n:PreDeployCommands / PostDeployCommands): e.g. hold the job queue
            // first and always release it at the end, also when a step failed.
            var pre = IbmTurnover.Core.Workflow.DeployCommand.Parse(cmp.Target.PreDeployCommands);
            var undoOf = new List<(PlanStep Pre, PlanStep Undo)>();
            for (var i = 0; i < pre.Count; i++)
            {
                var ps = new PlanStep { Phase = StepPhase.Setup, Rank = 20 + i, Kind = StepKind.Cl, Title = $"Before deploy: {pre[i].Command}", Command = pre[i].Command };
                _steps.Add(ps);
                if (pre[i].Undo is { } undo)
                {
                    var us = new PlanStep { Phase = StepPhase.Finally, Rank = i, Kind = StepKind.Cl, Title = $"After deploy: {undo}", Command = undo, AlwaysRun = true };
                    _steps.Add(us);
                    undoOf.Add((ps, us));
                }
            }
            var post = IbmTurnover.Core.Workflow.DeployCommand.Parse(cmp.Target.PostDeployCommands);
            for (var i = 0; i < post.Count; i++)
                _steps.Add(new PlanStep { Phase = StepPhase.Finally, Rank = 100 + i, Kind = StepKind.Cl, Title = $"After deploy: {post[i].Command}", Command = post[i].Command, AlwaysRun = true });

            var views = OrderByReference(_steps.Where(s => s.Phase == StepPhase.Routines).ToList(), diffs);
            var ordered = _steps.Where(s => s.Phase != StepPhase.Routines)
                .Concat(views)
                .OrderBy(s => s.Phase).ThenBy(s => s.Rank)
                .ToList();
            // Routines keep the topological order computed above (OrderBy is stable).
            for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i + 1;
            foreach (var (ps, us) in undoOf) us.RunIfSucceeded = ps.Order;
            return ordered;
        }

        /// <summary>Within the routine phase, create whatever another routine/view mentions first.</summary>
        private static IEnumerable<PlanStep> OrderByReference(List<PlanStep> steps, List<Difference> diffs)
        {
            var byDiff = diffs.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var ranked = steps.OrderBy(s => s.Rank).ToList();
            var groups = ranked.GroupBy(s => s.DiffId ?? s.Title).ToList();
            var names = groups.ToDictionary(g => g.Key, g => byDiff.TryGetValue(g.Key, out var d) ? d.Name : "", StringComparer.OrdinalIgnoreCase);
            var edges = groups.ToDictionary(g => g.Key, g =>
            {
                var text = string.Join("\n", g.Select(s => s.Command));
                return groups.Where(o => o.Key != g.Key && names[o.Key].Length > 0 &&
                        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_#@$]){Regex.Escape(names[o.Key])}(?![A-Za-z0-9_#@$])", RegexOptions.IgnoreCase))
                    .Select(o => o.Key).ToList();
            }, StringComparer.OrdinalIgnoreCase);
            var order = TopologicalSort.Sort(groups.Select(g => g.Key).ToList(), k => edges[k]);
            var rankFloor = 0;
            foreach (var key in order)
                foreach (var s in groups.First(g => g.Key == key))
                {
                    // Dependency order wins over the default rank, but a drop always precedes its own create.
                    s.Rank = Math.Max(s.Rank, rankFloor);
                    rankFloor = s.Rank;
                    yield return s;
                }
        }
    }
}

public static class TopologicalSort
{
    /// <summary>Kahn-style sort; dependencies first. Cycles fall back to input order instead of failing.</summary>
    public static List<T> Sort<T>(IReadOnlyList<T> items, Func<T, IEnumerable<T>> dependsOn) where T : notnull
    {
        var result = new List<T>();
        var state = new Dictionary<T, int>(); // 0 new, 1 visiting, 2 done
        void Visit(T n)
        {
            if (state.TryGetValue(n, out var s) && s != 0) return; // done, or a cycle: break it here
            state[n] = 1;
            foreach (var dep in dependsOn(n)) if (items.Contains(dep)) Visit(dep);
            state[n] = 2;
            result.Add(n);
        }
        foreach (var i in items) Visit(i);
        return result;
    }
}
