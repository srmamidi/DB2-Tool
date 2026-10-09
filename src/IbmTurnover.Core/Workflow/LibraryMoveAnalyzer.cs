using System.Text.RegularExpressions;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;

namespace IbmTurnover.Core.Workflow;

public sealed record MoveImpact(Severity Severity, string Library, string Object, string Kind, string Issue, string Action);

public sealed class LibraryMoveResult
{
    public string Environment { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool KeepOriginal { get; set; } = true;
    public int ObjectCount { get; set; }
    public List<MoveImpact> Impacts { get; set; } = new();
    public List<PlanStep> Steps { get; set; } = new();
    public string Script { get; set; } = "";
}

/// <summary>
/// Moves (or clones) a whole library to a new name on the same IBM i: CPYLIB keeps data, sequence values
/// and programs; then everything that hard-codes the old name (inside the library and in OTHER libraries)
/// is re-created against the new name. The old library is kept unless the caller opts in to deleting it.
/// </summary>
public sealed class LibraryMoveAnalyzer(CatalogService catalog, TurnoverOptions options)
{
    public async Task<LibraryMoveResult> AnalyzeAsync(string envKey, string fromLibrary, string toLibrary, bool keepOriginal, CancellationToken ct = default)
    {
        var env = options.Env(envKey);
        string from = fromLibrary.Trim().ToUpperInvariant(), to = toLibrary.Trim().ToUpperInvariant();
        if (!TurnoverOptions.IsValidLibraryName(from) || !TurnoverOptions.IsValidLibraryName(to))
            throw new TurnoverException("Library names must be 1-10 characters: A-Z, 0-9, _ # @ $, not starting with a digit.");
        if (from == to) throw new TurnoverException("From and To are the same library.");

        var snaps = (await catalog.LoadAsync(env, ct)).Libraries;
        var trace = new CatalogTrace();
        if (!snaps.TryGetValue(from, out var src))
            src = await catalog.Provider.GetSnapshotAsync(env, from, trace, ct) ?? throw new TurnoverException($"Library {from} was not found in {env.Key}.");
        var toExists = snaps.ContainsKey(to) || await catalog.Provider.GetSnapshotAsync(env, to, trace, ct) is not null;

        var map = new LibraryMap([new LibraryPair(from, from, to)], options.Environments, env);
        var result = new LibraryMoveResult { Environment = env.Key, From = from, To = to, KeepOriginal = keepOriginal };
        result.ObjectCount = src.Tables.Count + src.Views.Count + src.Indexes.Count + src.Sequences.Count + src.Routines.Count + src.Triggers.Count + src.Programs.Count;
        var steps = new List<PlanStep>();
        void Step(StepPhase ph, int rank, StepKind k, string title, string cmd, string? undo = null, bool destructive = false) =>
            steps.Add(new PlanStep { Phase = ph, Rank = rank, Kind = k, Title = title, Command = cmd, Rollback = undo, Destructive = destructive });
        void Impact(Severity s, string lib, string obj, string kind, string issue, string action) => result.Impacts.Add(new MoveImpact(s, lib, obj, kind, issue, action));

        if (toExists)
            Impact(Severity.Blocker, to, to, "Library", $"{to} already exists in {env.Key}. CPYLIB needs a new library.", "Pick another name or remove it first.");
        if (env.Libraries.Values.Contains(from, StringComparer.OrdinalIgnoreCase))
            Impact(Severity.Warning, from, from, "Config", $"{from} is mapped as logical library {env.LogicalFor(from)} for {env.Key}.", $"After the move, change Turnover:Environments so {env.LogicalFor(from)} → {to}, or promotions keep targeting {from}.");

        Step(StepPhase.PreCheck, 0, StepKind.Check, $"{to} does not exist yet", $"SELECT COUNT(*) FROM TABLE(QSYS2.OBJECT_STATISTICS('QSYS', '*LIB', '{to}')) X");
        Step(StepPhase.PreCheck, 1, StepKind.Check, $"No jobs hold locks in {from}",
            $"SELECT COUNT(*) FROM QSYS2.OBJECT_LOCK_INFO WHERE OBJECT_SCHEMA = '{from}' AND LOCK_STATE IN ('*EXCL', '*EXCLRD', '*SHRUPD')");
        Step(StepPhase.Deploy, 0, StepKind.Cl, $"Copy {from} → {to} with data (tables, sequences, programs, source)",
            $"CPYLIB FROMLIB({from}) TOLIB({to}) CRTLIB(*YES) DATA(*YES)", Ddl.Qcmdexc($"DLTLIB LIB({to})"));

        // Inside the copied library: anything whose text names the old library still points at it.
        foreach (var r in src.Routines) Recreate(r.Name, r.Kind == "FUNCTION" ? "Function" : "Procedure", r.Definition, to, 20);
        foreach (var v in src.Views) Recreate(v.Name, "View", v.Definition, to, 21);
        foreach (var t in src.Triggers) Recreate(t.Name, "Trigger", t.Definition, to, 22);
        foreach (var fk in src.ForeignKeys.Where(f => !f.RefLibrary.Equals(from, StringComparison.OrdinalIgnoreCase)))
            Impact(Severity.Info, to, fk.Name, "Foreign key", $"Copied constraint still points at parent {fk.RefLibrary}.{fk.RefTable} (outside the move).", "No action: the parent did not move.");
        foreach (var p in src.Programs.Where(x => x.FileReferences.Any(f => f.Library.Equals(from, StringComparison.OrdinalIgnoreCase))))
            Impact(Severity.Warning, to, p.Name, p.ObjectType, $"Opens files qualified with {from}; the copy in {to} still uses {from}.", "Change the source to *LIBL / EXTFILE and recompile.");

        // Outside the library: who depends on it.
        foreach (var other in snaps.Values.Where(s => !s.Library.Equals(from, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var fk in other.ForeignKeys.Where(f => f.RefLibrary.Equals(from, StringComparison.OrdinalIgnoreCase)))
            {
                Impact(Severity.Warning, other.Library, fk.Name, "Foreign key", $"{other.Library}.{fk.Table} references {from}.{fk.RefTable}.", $"Re-pointed at {to}.{fk.RefTable} (drop + add; Db2 re-verifies every existing row when the constraint is added).");
                var moved = new ForeignKeyDef { Name = fk.Name, Table = fk.Table, Columns = fk.Columns, RefLibrary = to, RefTable = fk.RefTable, RefColumns = fk.RefColumns, DeleteRule = fk.DeleteRule, UpdateRule = fk.UpdateRule };
                Step(StepPhase.Deploy, 30, StepKind.Sql, $"Drop {other.Library}.{fk.Name} (re-added below)", Ddl.DropConstraint(other.Library, fk.Table, fk.Name), Ddl.AddForeignKey(other.Library, fk, from));
                Step(StepPhase.Deploy, 31, StepKind.Sql, $"Re-add {other.Library}.{fk.Name} → {to}", Ddl.AddForeignKey(other.Library, moved, to), Ddl.DropConstraint(other.Library, fk.Table, fk.Name));
            }
            foreach (var v in other.Views.Where(v => Mentions(v.Definition, from))) Recreate(v.Name, "View", v.Definition, other.Library, 40);
            foreach (var r in other.Routines.Where(r => Mentions(r.Definition, from))) Recreate(r.Name, r.Kind == "FUNCTION" ? "Function" : "Procedure", r.Definition, other.Library, 41);
            foreach (var t in other.Triggers.Where(t => Mentions(t.Definition, from))) Recreate(t.Name, "Trigger", t.Definition, other.Library, 42);
            foreach (var p in other.Programs)
            {
                var explicitRefs = p.FileReferences.Where(f => f.Library.Equals(from, StringComparison.OrdinalIgnoreCase)).ToList();
                var liblRefs = p.FileReferences.Where(f => f.Library.StartsWith('*') && src.Tables.Any(t => t.SystemName.Equals(f.File, StringComparison.OrdinalIgnoreCase))).ToList();
                if (explicitRefs.Count > 0)
                    Impact(Severity.Blocker, other.Library, p.Name, p.ObjectType, $"Hard-codes {from}/{string.Join(", ", explicitRefs.Select(f => f.File))}.", "Change the source and recompile; moving the library breaks it.");
                else if (liblRefs.Count > 0)
                    Impact(Severity.Warning, other.Library, p.Name, p.ObjectType, $"Opens {string.Join(", ", liblRefs.Select(f => f.File))} through the library list.", $"Replace {from} with {to} in job descriptions (CHGJOBD INLLIBL) and CL that sets the library list.");
                if (p.BoundServicePrograms.Any(b => b.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)))
                    Impact(Severity.Blocker, other.Library, p.Name, p.ObjectType, $"Bound to a service program in {from}.", $"Re-bind: UPDPGM PGM({other.Library}/{p.Name}) MODULE(*NONE) BNDSRVPGM(({to}/...)).");
            }
        }

        if (!keepOriginal)
        {
            Step(StepPhase.PostCheck, 90, StepKind.Check, $"Nothing still references {from}", $"SELECT COUNT(*) FROM QSYS2.SYSVIEWDEP WHERE OBJECT_SCHEMA = '{from}' AND VIEW_SCHEMA <> '{from}'");
            Step(StepPhase.PostCheck, 99, StepKind.Cl, $"Delete original library {from}", $"DLTLIB LIB({from})", "-- restore " + from + " from the pre-move save (SAVLIB) taken by your backup process", destructive: true);
            Impact(Severity.Warning, from, from, "Library", $"{from} will be deleted after the move.", "Take a SAVLIB first. Deletion is irreversible.");
        }

        var ordered = steps.OrderBy(s => s.Phase).ThenBy(s => s.Rank).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i + 1;
        result.Steps = ordered;
        result.Script = $"-- Move {from} → {to} in {env.Key} ({result.ObjectCount} objects). CHECK steps must return 0.\n" +
                        string.Join("\n", ordered.Select(s => $"\n-- [{s.Order:D3}] {s.Phase} · {s.Title}{(s.Destructive ? "  (DESTRUCTIVE)" : "")}\n{DeploymentPlanner.AsSql(s)};"));
        return result;

        void Recreate(string name, string kind, string definition, string inLibrary, int rank)
        {
            var r = map.Remap(definition);
            if (r.Rewrites.Count == 0) return;
            if (!inLibrary.Equals(to, StringComparison.OrdinalIgnoreCase))
                Impact(Severity.Warning, inLibrary, name, kind, $"Refers to {from} ({r.Rewrites.Count} place(s)).", $"Re-created pointing at {to}.");
            Step(StepPhase.Routines, rank, StepKind.Sql, $"Re-create {kind.ToLowerInvariant()} {inLibrary}.{name} against {to}", r.Text,
                inLibrary.Equals(to, StringComparison.OrdinalIgnoreCase) ? null : definition);
        }
    }

    private static bool Mentions(string text, string lib) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_#@$]){Regex.Escape(lib)}(?![A-Za-z0-9_#@$])", RegexOptions.IgnoreCase);
}
