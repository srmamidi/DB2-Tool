using System.Text.RegularExpressions;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

public sealed class CompareInput
{
    public required TurnoverEnvironment Source { get; init; }
    public required TurnoverEnvironment Target { get; init; }
    public required IReadOnlyList<TurnoverEnvironment> AllEnvironments { get; init; }
    /// <summary>Libraries to diff. Everything else in the two environments is used only to resolve references.</summary>
    public required IReadOnlyList<LibraryPair> Pairs { get; init; }
    /// <summary>Every library of the source environment, keyed by physical library name.</summary>
    public required IReadOnlyDictionary<string, SchemaSnapshot> SourceSnapshots { get; init; }
    /// <summary>Every library of the target environment, keyed by physical library name.</summary>
    public required IReadOnlyDictionary<string, SchemaSnapshot> TargetSnapshots { get; init; }
    public required LibraryMap Map { get; init; }
    /// <summary>Tracked user profiles on each side (system-wide objects). Empty for library-to-library compares.</summary>
    public IReadOnlyList<UserProfileDef> SourceProfiles { get; init; } = [];
    public IReadOnlyList<UserProfileDef> TargetProfiles { get; init; } = [];
    public ProfileMap? Profiles { get; init; }
    /// <summary>Authorization lists, job schedules, system values and IFS files on each side (null = not read).</summary>
    public SystemObjects? SourceSystem { get; init; }
    public SystemObjects? TargetSystem { get; init; }
    /// <summary>Developer / personal library: objects that exist only in the target are not reported.</summary>
    public bool SourceOnly { get; init; }
    public CompareMode Mode { get; init; } = CompareMode.Promotion;
    /// <summary>Library mode: programs, DDS files, commands, data areas/queues, job descriptions and messages go here instead (master program library).</summary>
    public string? ProgramLibrary { get; init; }
    /// <summary>Library mode: where copied source members go (master source library).</summary>
    public string? SourceFileLibrary { get; init; }
}

public enum CompareMode { Promotion, Library, History }

/// <summary>
/// Compares a source environment with a target environment and explains, rule by rule, what will
/// break if the source is promoted as-is. Rule codes are documented in docs/ibm-turnover.html.
/// </summary>
public sealed partial class SchemaComparer
{
    public ComparisonResult Compare(CompareInput input) => new Run(input).Execute();

    private sealed partial class Run(CompareInput I)
    {
        private readonly List<Difference> _diffs = new();
        private readonly List<Finding> _global = new();
        private readonly Dictionary<string, Difference> _addedTables = new(StringComparer.OrdinalIgnoreCase);     // TGTLIB.TABLE
        private readonly Dictionary<string, Difference> _columnDiffs = new(StringComparer.OrdinalIgnoreCase);     // TGTLIB.TABLE.COLUMN
        private readonly Dictionary<string, Difference> _indexDiffs = new(StringComparer.OrdinalIgnoreCase);      // TGTLIB.INDEX
        private readonly Dictionary<string, Difference> _programDiffs = new(StringComparer.OrdinalIgnoreCase);    // TGTLIB/NAME/TYPE (*CMD FOO and *PGM FOO are different objects)
        private readonly Dictionary<string, Difference> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Difference>> _formatChanged = new(StringComparer.OrdinalIgnoreCase); // TGTLIB/SYSNAME -> diffs causing it
        private readonly Dictionary<string, Difference> _addedSequences = new(StringComparer.OrdinalIgnoreCase);  // TGTLIB.SEQ
        private readonly Dictionary<string, SchemaSnapshot> _emptyTargets = new(StringComparer.OrdinalIgnoreCase);

        private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
        private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public ComparisonResult Execute()
        {
            if (I.Mode == CompareMode.History)
                _global.Add(Finding.Of(Severity.Info, "HIST001", $"History comparison inside {I.Source.Key}: what changed between two points in time."));
            else CheckPath();
            var pairs = new List<(LibraryPair Pair, SchemaSnapshot S, SchemaSnapshot T)>();
            foreach (var p in I.Pairs)
            {
                if (I.Mode != CompareMode.History && Eq(p.SourceLibrary, p.TargetLibrary) && Eq(I.Source.Host, I.Target.Host))
                {
                    _global.Add(Finding.Of(Severity.Blocker, "LIB002", $"{p.Logical}: source and target are the same library ({p.SourceLibrary}) on the same host. Nothing to promote; a deployment would rewrite the library in place."));
                    continue;
                }
                if (!I.SourceSnapshots.TryGetValue(p.SourceLibrary, out var s))
                {
                    _global.Add(Finding.Of(Severity.Blocker, "LIB003", $"Library {p.SourceLibrary} could not be read in {I.Source.Key}. It may not exist or the profile lacks *USE authority."));
                    continue;
                }
                pairs.Add((p, s, TargetOrEmpty(p)));
            }

            foreach (var (p, s, t) in pairs) CompareTables(p, s, t);
            foreach (var (p, s, t) in pairs) CompareSequences(p, s, t);
            foreach (var (p, s, t) in pairs) CompareIndexes(p, s, t);
            foreach (var (p, s, t) in pairs) CompareForeignKeys(p, s, t);
            // Personal → master: program-type objects may live in a different master library than the tables.
            var programPairs = pairs;
            if (I.Mode == CompareMode.Library && !string.IsNullOrEmpty(I.ProgramLibrary) && pairs.Count == 1 && !Eq(I.ProgramLibrary, pairs[0].Pair.TargetLibrary))
            {
                var pp = pairs[0].Pair with { TargetLibrary = I.ProgramLibrary! };
                programPairs = [(pp, pairs[0].S, TargetOrEmpty(pp))];
            }
            foreach (var (p, s, t) in programPairs) ComparePrograms(p, s, t);
            CheckLevelChecks();
            foreach (var (p, s, t) in pairs) CompareRoutines(p, s, t);
            foreach (var (p, s, t) in pairs) CompareViews(p, s, t);
            foreach (var (p, s, t) in pairs) CompareTriggers(p, s, t);
            foreach (var (p, s, t) in pairs) CompareAliases(p, s, t);
            foreach (var (p, s, t) in programPairs) CompareDataAreas(p, s, t);
            foreach (var (p, s, t) in programPairs) CompareDataQueues(p, s, t);
            foreach (var (p, s, t) in programPairs) CompareJobDescriptions(p, s, t);
            foreach (var (p, s, t) in programPairs) CompareMessages(p, s, t);
            foreach (var (p, s, t) in programPairs) CompareBindingDirectories(p, s, t);
            foreach (var (p, s, t) in pairs) CompareAuthorities(p, s, t);
            foreach (var (p, _, t) in pairs) CheckPending(p, t);
            CompareProfiles();
            CompareSystemObjects();
            CheckDroppedColumnUsers();
            if (I.SourceOnly) DropTargetOnlyObjects(pairs);
            // "What changed between two points in time": standing drift (a generator already behind its data, the same
            // index under two names, recompiles a change would need) is not a change, so it is left out of history.
            // A data-area value that changed between the two points in time is a real change, not standing drift.
            if (I.Mode == CompareMode.History) _diffs.RemoveAll(d => d.Change == ChangeKind.Drift && d.Kind != ObjectKind.DataArea);
            foreach (var d in _diffs) AttributeDiff.Fill(d);

            // Profiles and partition-wide objects are part of "the target changed after approval" too.
            var fingerprint = string.Join("-", I.TargetSnapshots.OrderBy(k => k.Key, Ci).Select(k => k.Value.Fingerprint()))
                + "|" + Fingerprints.Of(I.TargetProfiles.OrderBy(x => x.Name, Ci).ToList()) + "|" + (I.TargetSystem?.Fingerprint() ?? "");
            return new ComparisonResult
            {
                Source = I.Source,
                Target = I.Target,
                Pairs = I.Pairs.ToList(),
                GlobalFindings = _global,
                Differences = _diffs.OrderBy(d => d.Kind).ThenBy(d => d.Logical).ThenBy(d => d.DisplayName, Ci).ToList(),
                SourceOnly = I.SourceOnly,
                SourceFileLibrary = I.SourceFileLibrary,
                TargetLibrariesPresent = I.TargetSnapshots.Keys.ToList(),
                TargetFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint)))[..16],
            };
        }

        // ---------------------------------------------------------------- path & libraries

        private void CheckPath()
        {
            if (Eq(I.Source.Key, I.Target.Key))
            {
                _global.Add(Finding.Of(Severity.Info, "PATH003", $"Library-to-library propagation inside {I.Source.Key}."));
                return;
            }
            var delta = (int)I.Target.Tier - (int)I.Source.Tier;
            if (delta > 1)
                _global.Add(Finding.Of(Severity.Blocker, "PATH001",
                    $"{I.Source.Key} → {I.Target.Key} skips {delta - 1} tier(s). Every change must pass through each tier so it is tested in QA and Stage before Prod.",
                    "Promote to the next tier first."));
            else if (delta < 0)
                _global.Add(Finding.Of(Severity.Warning, "PATH002",
                    $"Downward propagation ({I.Source.Key} → {I.Target.Key}). Use only to retrofit an emergency fix back into lower environments.",
                    "Confirm the lower environment has no newer work that this would overwrite."));
            else if (delta == 0)
                _global.Add(Finding.Of(Severity.Info, "PATH004", $"{I.Source.Key} and {I.Target.Key} are the same tier."));
        }

        private SchemaSnapshot TargetOrEmpty(LibraryPair p)
        {
            if (I.TargetSnapshots.TryGetValue(p.TargetLibrary, out var t)) return t;
            var empty = new SchemaSnapshot { EnvironmentKey = I.Target.Key, Library = p.TargetLibrary, Logical = p.Logical };
            _emptyTargets[p.TargetLibrary] = empty;
            Add(new Difference
            {
                Id = Difference.MakeId(ObjectKind.Library, p.Logical, p.TargetLibrary),
                Kind = ObjectKind.Library, Change = ChangeKind.Added, Logical = p.Logical,
                SourceLibrary = p.SourceLibrary, TargetLibrary = p.TargetLibrary, Name = p.TargetLibrary,
                SourceDdl = $"CREATE SCHEMA {p.TargetLibrary}",
                Details = { $"Library {p.TargetLibrary} does not exist in {I.Target.Key}." },
                Findings = { Finding.Of(Severity.Warning, "LIB001", $"Library {p.TargetLibrary} will be created as an SQL schema (with journal QSQJRN).", "Confirm the library name, owner and authority (*PUBLIC *EXCLUDE is recommended).") },
            });
            return empty;
        }

        private SchemaSnapshot? TargetLib(string lib) =>
            I.TargetSnapshots.TryGetValue(lib, out var s) ? s : _emptyTargets.GetValueOrDefault(lib);

        private bool PairedTarget(string tgtLib) => I.Pairs.Any(p => Eq(p.TargetLibrary, tgtLib));

        private Difference NewDiff(LibraryPair p, ObjectKind kind, ChangeKind change, string name, string? parent = null) => new()
        {
            Id = Difference.MakeId(kind, p.Logical, name, parent),
            Kind = kind, Change = change, Logical = p.Logical,
            SourceLibrary = p.SourceLibrary, TargetLibrary = p.TargetLibrary, Name = name, Parent = parent,
        };

        private void Add(Difference d)
        {
            // Two rules can describe the same object (e.g. a recompile for a level check and a source change). Merge them.
            // Indexed by id: a linear search here made a first comparison into an empty target quadratic.
            if (!_byId.TryGetValue(d.Id, out var existing)) { _diffs.Add(d); _byId[d.Id] = d; return; }
            existing.Findings.AddRange(d.Findings);
            existing.Details.AddRange(d.Details);
        }

        private void MarkFormatChange(LibraryPair p, TableDef target, Difference cause) => MarkFormatChangeKey($"{p.TargetLibrary}/{target.SystemName}", cause);

        private void MarkFormatChangeKey(string key, Difference cause)
        {
            if (!_formatChanged.TryGetValue(key, out var list)) _formatChanged[key] = list = new();
            list.Add(cause);
        }

        private void LibraryRewriteFindings(Difference d, RemapResult r, string what, params string[] ownNames)
        {
            // The object's own qualified name (CREATE ... LIB.NAME / SPECIFIC LIB.NAME) and EXTERNAL NAME are expected
            // to be qualified; only references to OTHER objects are worth a warning.
            var relevant = r.Rewrites.Where(x => !x.InExternalName && !(x.Object is not null && ownNames.Contains(x.Object, Ci))).ToList();
            var code = relevant.Where(x => !x.InStringLiteral).ToList();
            var lit = relevant.Where(x => x.InStringLiteral).ToList();
            if (code.Count > 0)
                d.Findings.Add(Finding.Of(Severity.Warning, "RTN002",
                    $"{what} hard-codes source libraries: {Summ(code.Select(x => $"{x.From}→{x.To} (line {x.Line})"))}. Rewritten automatically.",
                    "Prefer unqualified names resolved through SET PATH / the library list so the same code runs in every environment.", autoFixed: true));
            if (lit.Count > 0)
                d.Findings.Add(Finding.Of(Severity.Warning, "RTN007",
                    $"Library name inside a string literal (dynamic SQL?) was rewritten: {Summ(lit.Select(x => $"{x.From}→{x.To} (line {x.Line})"))}.",
                    "Read the statement and confirm the rewrite is what you want.", autoFixed: true));
            foreach (var f in r.Foreign.GroupBy(x => x.Library, Ci))
                d.Findings.Add(Finding.Of(Severity.Blocker, "RTN003",
                    $"{what} references {f.Key}, which belongs to {f.First().OwnerEnvironment} (line {string.Join(", ", f.Select(x => x.Line))}). In {I.Target.Key} it would read or write {f.First().OwnerEnvironment} data.",
                    "Replace the reference with the logical library resolved through the library list."));
        }

        private static string Summ(IEnumerable<string> items)
        {
            var list = items.Distinct().ToList();
            return list.Count <= 4 ? string.Join(", ", list) : string.Join(", ", list.Take(4)) + $" and {list.Count - 4} more";
        }

        // ---------------------------------------------------------------- tables & columns

        private void CompareTables(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var st in s.Tables)
            {
                var tt = t.Tables.FirstOrDefault(x => Eq(x.Name, st.Name));
                if (tt is null)
                {
                    var d = NewDiff(p, ObjectKind.Table, ChangeKind.Added, st.Name);
                    d.Source = st;
                    d.SourceDdl = Ddl.CreateTable(p.TargetLibrary, st);
                    d.Details.Add($"{st.Columns.Count} columns, system name {st.SystemName}");
                    d.Findings.Add(Finding.Of(Severity.Info, "TBL001", $"New table {st.Name} will be created in {p.TargetLibrary}."));
                    var clash = t.Tables.FirstOrDefault(x => Eq(x.SystemName, st.SystemName));
                    if (clash is not null)
                        d.Findings.Add(Finding.Of(Severity.Blocker, "TBL006",
                            $"System name {st.SystemName} is already used in {p.TargetLibrary} by {clash.Name}. CREATE TABLE fails with SQL0601.",
                            "Rename one of the objects' system names."));
                    Add(d);
                    _addedTables[$"{p.TargetLibrary}.{st.Name}"] = d;
                    continue;
                }
                CompareTable(p, st, tt);
            }

            foreach (var tt in t.Tables.Where(x => !s.Tables.Any(y => Eq(y.Name, x.Name))))
            {
                var d = NewDiff(p, ObjectKind.Table, ChangeKind.Removed, tt.Name);
                d.Target = tt;
                d.TargetDdl = Ddl.CreateTable(p.TargetLibrary, tt);
                d.DefaultSelected = false;
                d.Destructive = true;
                d.Details.Add($"{tt.RowCount:N0} rows in {I.Target.Key}");
                d.Findings.Add(Finding.Of(Severity.Warning, "TBL002",
                    $"{tt.Name} exists only in {I.Target.Key} ({tt.RowCount:N0} rows). It is never dropped unless you tick it.",
                    "Find out who created it. Common causes: a one-off fix table, or work not yet retrofitted into source."));
                Add(d);
            }
        }

        private void CompareTable(LibraryPair p, TableDef st, TableDef tt)
        {
            var table = NewDiff(p, ObjectKind.Table, ChangeKind.Modified, st.Name);
            table.Source = st; table.Target = tt;
            table.SourceDdl = Ddl.CreateTable(p.TargetLibrary, st);
            table.TargetDdl = Ddl.CreateTable(p.TargetLibrary, tt);

            if (!Eq(st.SystemName, tt.SystemName))
            {
                table.Details.Add($"System name {tt.SystemName} → {st.SystemName}");
                table.Findings.Add(Finding.Of(Severity.Blocker, "TBL003",
                    $"System (10-character) name differs: {I.Source.Key} {st.SystemName}, {I.Target.Key} {tt.SystemName}. RPG, CL, DDS logical files, CPYF and OVRDBF open files by system name, so programs open the wrong file or fail with CPF4101.",
                    $"Script renames it with RENAME TABLE ... TO SYSTEM NAME {st.SystemName}. Confirm nothing in {I.Target.Key} still uses {tt.SystemName}."));
                MarkFormatChange(p, tt, table);
            }
            if (st.Journaled && !tt.Journaled)
            {
                table.Details.Add("Journaling: on in source, off in target");
                table.Findings.Add(Finding.Of(Severity.Warning, "TBL004",
                    $"{tt.Name} is not journaled in {I.Target.Key}. Programs that run under commitment control fail with SQL7008, and there is no change history for recovery.",
                    "Script starts journaling (STRJRNPF) to the library's QSQJRN.", autoFixed: true));
            }
            if (!Eq(st.Text, tt.Text))
            {
                table.Details.Add("Table text differs");
                table.Findings.Add(Finding.Of(Severity.Info, "TBL007", "Table description (LABEL ON TABLE) differs."));
            }
            if (table.Findings.Count > 0) Add(table);

            CompareColumns(p, st, tt, table);
            CompareChecks(p, st, tt);
        }

        private void CompareColumns(LibraryPair p, TableDef st, TableDef tt, Difference tableDiff)
        {
            var added = st.Columns.Where(c => tt.Column(c.Name) is null && !tt.Columns.Any(x => Eq(x.Name, c.Name))).ToList();
            var removed = tt.Columns.Where(c => !st.Columns.Any(x => Eq(x.Name, c.Name))).ToList();

            // Rename heuristic: a column that disappeared and one that appeared with the same type at the same position.
            foreach (var r in removed.ToList())
            {
                var a = added.FirstOrDefault(x => x.TypeSignature == r.TypeSignature && (x.Ordinal == r.Ordinal || Eq(x.SystemName, r.SystemName)));
                if (a is null) continue;
                added.Remove(a); removed.Remove(r);
                var d = NewDiff(p, ObjectKind.Column, ChangeKind.Renamed, a.Name, st.Name);
                d.Source = a; d.Target = r; d.Destructive = true;
                d.SourceDdl = Ddl.ColumnSpec(a); d.TargetDdl = Ddl.ColumnSpec(r);
                d.Details.Add($"{r.Name} → {a.Name}");
                d.Findings.Add(Finding.Of(Severity.Warning, "COL007",
                    $"{r.Name} looks renamed to {a.Name} (same type {a.TypeSignature}, same position). Script keeps the data: ADD {a.Name}, copy values, DROP {r.Name} RESTRICT.",
                    $"If it is really a new column plus a dropped one, untick this and handle them separately. RPG programs that use field {r.SystemName} must be changed before they are recompiled."));
                d.DependsOn.AddRange(DependsOnTable(p, st.Name));
                RegisterColumn(p, st, tt, d);
            }

            foreach (var a in added)
            {
                var d = NewDiff(p, ObjectKind.Column, ChangeKind.Added, a.Name, st.Name);
                d.Source = a;
                d.SourceDdl = Ddl.ColumnSpec(a);
                d.Details.Add(a.TypeSignature + (a.Nullable ? "" : " NOT NULL") + (a.Default is null ? "" : " DEFAULT " + a.Default));
                d.Findings.Add(Finding.Of(Severity.Info, "COL001", $"New column {a.Name}."));
                if (!a.Nullable && a.Default is null && !a.IsIdentity && tt.RowCount > 0)
                    d.Findings.Add(Finding.Of(Severity.Blocker, "COL002",
                        $"{a.Name} is NOT NULL with no DEFAULT, and {tt.Name} has {tt.RowCount:N0} rows in {I.Target.Key}. ALTER TABLE fails (SQL0407): existing rows have no value to put in it.",
                        "Give the column a DEFAULT in source, or add it nullable, back-fill, then set NOT NULL."));
                RegisterColumn(p, st, tt, d);
            }

            foreach (var r in removed)
            {
                var d = NewDiff(p, ObjectKind.Column, ChangeKind.Removed, r.Name, st.Name);
                d.Target = r; d.TargetDdl = Ddl.ColumnSpec(r);
                d.DefaultSelected = false; d.Destructive = true;
                d.Findings.Add(Finding.Of(Severity.Warning, "COL013",
                    $"{r.Name} exists only in {I.Target.Key}. Dropping it deletes the data in {tt.RowCount:N0} rows and breaks any program still using it.",
                    "Tick only after confirming no program or report reads it. A backup copy is taken first."));
                RegisterColumn(p, st, tt, d);
            }

            foreach (var sc in st.Columns)
            {
                var tc = tt.Columns.FirstOrDefault(x => Eq(x.Name, sc.Name));
                if (tc is null) continue;
                var d = NewDiff(p, ObjectKind.Column, ChangeKind.Modified, sc.Name, st.Name);
                d.Source = sc; d.Target = tc;
                d.SourceDdl = Ddl.ColumnSpec(sc); d.TargetDdl = Ddl.ColumnSpec(tc);
                var formatChange = false;

                switch (TypeCompat.Classify(tc, sc))
                {
                    case TypeChange.Same: break;
                    case TypeChange.Widening:
                        d.Details.Add($"Type {tc.TypeSignature} → {sc.TypeSignature} (widening)");
                        d.Findings.Add(Finding.Of(Severity.Info, "COL003", $"{sc.Name} widens from {tc.TypeSignature} to {sc.TypeSignature}. No data loss."));
                        formatChange = true; break;
                    case TypeChange.Narrowing:
                        d.Details.Add($"Type {tc.TypeSignature} → {sc.TypeSignature} (NARROWING)");
                        d.Findings.Add(Finding.Of(Severity.Blocker, "COL003",
                            $"{sc.Name} narrows from {tc.TypeSignature} to {sc.TypeSignature}. Existing {I.Target.Key} values that do not fit are truncated or make ALTER fail.",
                            "A pre-check counts rows that would not fit; it must return 0. Confirm the narrowing is intended."));
                        var chk = TypeCompat.TruncationCheck(p.TargetLibrary, tt.Name, tc, sc);
                        if (chk is not null) d.PreChecks.Add(new PreCheck($"{tt.Name}.{sc.Name} values fit {sc.TypeSignature}", chk));
                        formatChange = true; break;
                    case TypeChange.SemanticChange:
                        d.Details.Add($"Type {tc.TypeSignature} → {sc.TypeSignature}");
                        d.Findings.Add(Finding.Of(Severity.Warning, "COL011", $"{sc.Name} changes from {tc.TypeSignature} to {sc.TypeSignature}: trailing-blank and comparison behaviour change."));
                        formatChange = true; break;
                    case TypeChange.Incompatible:
                        d.Details.Add($"Type {tc.TypeSignature} → {sc.TypeSignature} (INCOMPATIBLE)");
                        d.Findings.Add(Finding.Of(Severity.Blocker, "COL010",
                            $"{sc.Name} changes type family ({tc.TypeSignature} → {sc.TypeSignature}). ALTER cannot convert the data safely.",
                            "Add a new column, convert with an UPDATE you have tested, then drop the old column."));
                        formatChange = true; break;
                }
                if (tc.Nullable && !sc.Nullable)
                {
                    d.Details.Add("NULL → NOT NULL");
                    d.Findings.Add(Finding.Of(Severity.Warning, "COL004", $"{sc.Name} becomes NOT NULL. Rows holding NULL make the ALTER fail.", "A pre-check counts NULLs; back-fill them first."));
                    d.PreChecks.Add(new PreCheck($"{tt.Name}.{sc.Name} has no NULLs", $"SELECT COUNT(*) FROM {Ddl.Q(p.TargetLibrary, tt.Name)} WHERE {Ddl.C(sc.Name)} IS NULL"));
                    formatChange = true;
                }
                else if (!tc.Nullable && sc.Nullable)
                {
                    d.Details.Add("NOT NULL → NULL");
                    d.Findings.Add(Finding.Of(Severity.Info, "COL004", $"{sc.Name} becomes nullable. RPG programs need null-indicator support (ALWNULL) to read it."));
                    formatChange = true;
                }
                if (!Eq(tc.Default, sc.Default))
                {
                    d.Details.Add($"Default {tc.Default ?? "none"} → {sc.Default ?? "none"}");
                    d.Findings.Add(Finding.Of(Severity.Info, "COL005", $"{sc.Name} default changes."));
                }
                if (tc.Ccsid != sc.Ccsid)
                {
                    d.Details.Add($"CCSID {tc.Ccsid?.ToString() ?? "job default"} → {sc.Ccsid?.ToString() ?? "job default"}");
                    d.Findings.Add(Finding.Of(Severity.Warning, "COL006", $"{sc.Name} CCSID changes. Data is converted; 65535 (binary) to a text CCSID changes what RPG sees byte-for-byte."));
                    formatChange = true;
                }
                if (!Eq(tc.Text, sc.Text) || !Eq(tc.Heading, sc.Heading))
                {
                    d.Details.Add(!Eq(tc.Text, sc.Text) ? "Column text differs" : "Column heading differs");
                    d.Findings.Add(Finding.Of(Severity.Info, "COL015", $"{sc.Name} text or heading (LABEL ON COLUMN) differs. Shown in query tools, DFU and RPG prompts."));
                }
                if (!Eq(tc.SystemName, sc.SystemName))
                {
                    d.Details.Add($"System column name {tc.SystemName} → {sc.SystemName}");
                    d.Findings.Add(Finding.Of(Severity.Blocker, "COL008",
                        $"System column name differs ({I.Source.Key} {sc.SystemName}, {I.Target.Key} {tc.SystemName}). RPG programs use the system name as the field name, so they do not compile or read the wrong field.",
                        "Align the FOR COLUMN name in source, or rename the column in target."));
                    formatChange = true;
                }
                if (tc.IsIdentity != sc.IsIdentity)
                {
                    d.Details.Add(sc.IsIdentity ? "Becomes IDENTITY" : "Stops being IDENTITY");
                    d.Findings.Add(Finding.Of(Severity.Warning, "COL012", $"Identity attribute of {sc.Name} differs between environments. INSERTs that pass a value behave differently."));
                }
                // Data drift that exists regardless of what source looks like: the generator is behind the data.
                if (tc.IsIdentity && tc.IdentityNextValue is long next && tc.MaxValue is long max && next <= max)
                {
                    d.Details.Add($"Identity next value {next:N0} ≤ MAX({tc.Name}) {max:N0}");
                    d.RestartWith = max + 1;
                    d.Findings.Add(Finding.Of(Severity.Blocker, "COL009",
                        $"Identity on {tt.Name}.{tc.Name} in {I.Target.Key} will hand out {next:N0}, but rows already go up to {max:N0}. The next INSERT fails with a duplicate key (SQL0803).",
                        $"Script restarts the identity at {max + 1:N0}.", autoFixed: true));
                }

                if (d.Findings.Count == 0) continue;
                if (d.Findings.All(f => f.Code == "COL009")) d.Change = ChangeKind.Drift;
                if (formatChange) RegisterColumn(p, st, tt, d);
                else { d.TableSystemName = tt.SystemName; Add(d); _columnDiffs[$"{p.TargetLibrary}.{st.Name}.{sc.Name}"] = d; }
            }
        }

        private void RegisterColumn(LibraryPair p, TableDef st, TableDef tt, Difference d)
        {
            d.TableSystemName = tt.SystemName;
            if (tt.IsDds && !d.Findings.Any(f => f.Code == "DDS003"))
                d.Findings.Add(Finding.Of(Severity.Warning, "DDS003",
                    $"{tt.Name} is a DDS-described file. ALTER TABLE works, but its DDS source no longer matches, and the next CRTPF/CHGPF from source undoes this change.",
                    "Change the DDS source and use CHGPF, or convert the file to SQL DDL first."));
            Add(d);
            _columnDiffs[$"{p.TargetLibrary}.{st.Name}.{d.Name}"] = d;
            MarkFormatChange(p, tt, d);
        }

        private IEnumerable<string> DependsOnTable(LibraryPair p, string table) =>
            _addedTables.TryGetValue($"{p.TargetLibrary}.{table}", out var d) ? [d.Id] : [];

        private IEnumerable<string> DependsOnColumns(string tgtLib, string table, IEnumerable<string> columns) =>
            columns.Select(c => _columnDiffs.GetValueOrDefault($"{tgtLib}.{table}.{c}"))
                   .Where(d => d is not null && d.Change is ChangeKind.Added or ChangeKind.Renamed or ChangeKind.Modified)
                   .Select(d => d!.Id);

        /// <summary>
        /// SQL routines, views and triggers are validated when they are created, so every new table, sequence or
        /// column they mention must ship in the same package (or the CREATE fails with SQL0204 / SQL0206).
        /// </summary>
        private IEnumerable<string> DependsOnMentioned(string text, string ownLib)
        {
            var ids = SqlText.Identifiers(text);
            bool Word(string w) => ids.Contains(w);
            bool Mentions(string lib, string name) => Word($"{lib}.{name}") || (Eq(lib, ownLib) && Word(name));
            var found = new List<string>();
            foreach (var (key, d) in _addedTables.Concat(_addedSequences))
            {
                var dot = key.IndexOf('.');
                if (Mentions(key[..dot], key[(dot + 1)..])) found.Add(d.Id);
            }
            foreach (var d in _columnDiffs.Values.Where(x => x.Change is ChangeKind.Added or ChangeKind.Renamed))
                if (Mentions(d.TargetLibrary, d.Parent!) && Word(d.Name)) found.Add(d.Id);
            return found.Distinct();
        }

        // ---------------------------------------------------------------- sequences

        private void CompareSequences(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var ss in s.Sequences)
            {
                var ts = t.Sequences.FirstOrDefault(x => Eq(x.Name, ss.Name));
                var boundTable = ss.BoundTable ?? ts?.BoundTable;
                var boundColumn = ss.BoundColumn ?? ts?.BoundColumn;
                var targetTable = boundTable is null ? null : t.Table(boundTable);
                var targetMax = boundColumn is null ? null : targetTable?.Column(boundColumn)?.MaxValue;

                if (ts is null)
                {
                    var d = NewDiff(p, ObjectKind.Sequence, ChangeKind.Added, ss.Name);
                    d.Source = ss;
                    var start = targetMax is long m ? m + ss.Increment : ss.Start;
                    d.SourceDdl = Ddl.CreateSequence(p.TargetLibrary, ss, start);
                    d.Details.Add($"START WITH {start:N0}");
                    if (targetMax is long mx)
                        d.Findings.Add(Finding.Of(Severity.Info, "SEQ006", $"Start computed from {I.Target.Key} data: MAX({boundTable}.{boundColumn}) = {mx:N0}, so the sequence starts at {start:N0}. The {I.Source.Key} current value ({ss.NextValue?.ToString("N0") ?? "n/a"}) is never copied."));
                    else if (targetTable is { RowCount: > 0 })
                        d.Findings.Add(Finding.Of(Severity.Warning, "SEQ007", $"{boundTable} has {targetTable.RowCount:N0} rows in {I.Target.Key} but MAX({boundColumn}) is unknown. Starting at {start:N0} may collide.", "Check MAX before deploying or add a sequence binding so the tool reads it."));
                    else
                        d.Findings.Add(Finding.Of(Severity.Info, "SEQ001", $"New sequence; starts at {start:N0} (source definition). Environment values are never copied."));
                    Add(d);
                    _addedSequences[$"{p.TargetLibrary}.{ss.Name}"] = d;
                    continue;
                }

                var diff = NewDiff(p, ObjectKind.Sequence, ChangeKind.Modified, ss.Name);
                diff.Source = ss; diff.Target = ts;
                diff.SourceDdl = Ddl.Describe(ss, p.TargetLibrary); diff.TargetDdl = Ddl.Describe(ts, p.TargetLibrary);
                var attr = new List<string>();
                if (ss.Increment != ts.Increment) attr.Add($"INCREMENT {ts.Increment} → {ss.Increment}");
                if (ss.MinValue != ts.MinValue) attr.Add($"MINVALUE {ts.MinValue?.ToString() ?? "none"} → {ss.MinValue?.ToString() ?? "none"}");
                if (ss.MaxValue != ts.MaxValue) attr.Add($"MAXVALUE {ts.MaxValue?.ToString() ?? "none"} → {ss.MaxValue?.ToString() ?? "none"}");
                if (ss.Cache != ts.Cache) attr.Add($"CACHE {ts.Cache} → {ss.Cache}");
                if (ss.Cycle != ts.Cycle) attr.Add($"{(ts.Cycle ? "CYCLE" : "NO CYCLE")} → {(ss.Cycle ? "CYCLE" : "NO CYCLE")}");
                if (attr.Count > 0)
                {
                    diff.Details.AddRange(attr);
                    var risky = ss.Increment != ts.Increment || ss.Cycle != ts.Cycle || ss.MaxValue != ts.MaxValue;
                    diff.Findings.Add(Finding.Of(risky ? Severity.Warning : Severity.Info, "SEQ002",
                        $"Sequence attributes differ ({string.Join(", ", attr)}). Script ALTERs the attributes only; the current value in {I.Target.Key} is kept."));
                }
                if (!Eq(ss.DataType, ts.DataType))
                {
                    diff.Details.Add($"Type {ts.DataType} → {ss.DataType}");
                    diff.Findings.Add(Finding.Of(Severity.Warning, "SEQ004", $"Data type differs ({ts.DataType} vs {ss.DataType}). Db2 cannot ALTER a sequence's type; it must be dropped and re-created at the current value.", "Handle manually: DROP and CREATE ... START WITH the current target value."));
                }
                if (ss.Cycle && boundColumn is not null)
                    diff.Findings.Add(Finding.Of(Severity.Warning, "SEQ005", $"{ss.Name} feeds {boundTable}.{boundColumn} and is defined CYCLE: after it wraps it hands out values that already exist."));
                if (ts.NextValue is long next && targetMax is long max && next <= max)
                {
                    diff.Details.Add($"Next value {next:N0} ≤ MAX({boundColumn}) {max:N0}");
                    diff.RestartWith = max + ts.Increment;
                    diff.Findings.Add(Finding.Of(Severity.Blocker, "SEQ003",
                        $"In {I.Target.Key}, {ss.Name} will return {next:N0}, but {boundTable}.{boundColumn} already holds values up to {max:N0}. The next INSERT using it fails with a duplicate key (SQL0803). Usually caused by copying data in without moving the sequence.",
                        $"Script restarts the sequence at {max + ts.Increment:N0}.", autoFixed: true));
                }
                else if (ts.NextValue is null && boundColumn is not null && targetTable is not null)
                {
                    // Live catalogs cannot read a sequence's next value without consuming it, so check at deploy time.
                    diff.Details.Add("Next value checked at deploy time");
                    diff.Findings.Add(Finding.Of(Severity.Info, "SEQ009",
                        $"{ss.Name} feeds {boundTable}.{boundColumn}. A pre-check confirms the sequence is ahead of MAX({boundColumn}) in {I.Target.Key} (it uses up one sequence value)."));
                    diff.PreChecks.Add(new PreCheck($"{ss.Name} is ahead of {boundTable}.{boundColumn}",
                        $"SELECT CASE WHEN NEXT VALUE FOR {Ddl.Q(p.TargetLibrary, ss.Name)} > COALESCE((SELECT MAX({boundColumn}) FROM {Ddl.Q(p.TargetLibrary, targetTable.Name)}), 0) THEN 0 ELSE 1 END FROM SYSIBM.SYSDUMMY1"));
                }
                if (diff.Findings.Count == 0) continue;
                if (attr.Count == 0 && diff.Findings.All(f => f.Code is "SEQ003" or "SEQ009")) diff.Change = ChangeKind.Drift;
                Add(diff);
            }

            foreach (var ts in t.Sequences.Where(x => !s.Sequences.Any(y => Eq(y.Name, x.Name))))
            {
                var d = NewDiff(p, ObjectKind.Sequence, ChangeKind.Removed, ts.Name);
                d.Target = ts; d.TargetDdl = Ddl.Describe(ts, p.TargetLibrary);
                d.DefaultSelected = false;
                d.Findings.Add(Finding.Of(Severity.Info, "SEQ008", $"{ts.Name} exists only in {I.Target.Key}. Not dropped."));
                Add(d);
            }
        }

        // ---------------------------------------------------------------- indexes

        private void CompareIndexes(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            var matchedTarget = new HashSet<IndexDef>();
            foreach (var si in s.Indexes)
            {
                var ti = t.Indexes.FirstOrDefault(x => Eq(x.Name, si.Name));
                var tt = t.Table(si.Table);
                if (ti is not null)
                {
                    matchedTarget.Add(ti);
                    if (ti.Signature == si.Signature) continue;
                    var d = NewDiff(p, ObjectKind.Index, ChangeKind.Modified, si.Name);
                    d.Source = si; d.Target = ti;
                    d.SourceDdl = Ddl.CreateIndex(p.TargetLibrary, si); d.TargetDdl = Ddl.CreateIndex(p.TargetLibrary, ti);
                    d.Details.Add($"Keys {Keys(ti)} → {Keys(si)}");
                    if (si.IsUnique != ti.IsUnique) d.Details.Add(si.IsUnique ? "Becomes UNIQUE" : "Stops being UNIQUE");
                    d.Findings.Add(Finding.Of(Severity.Warning, "IDX002",
                        $"Index {si.Name} has different keys in {I.Target.Key}. It is dropped and rebuilt; on {tt?.RowCount ?? 0:N0} rows that holds an exclusive lock on {si.Table} while it builds.",
                        "Deploy in a quiet window. Query plans that use this index are re-optimized."));
                    if (si.IsUnique && !ti.IsUnique && tt is { RowCount: > 0 }) UniqueOnExistingRows(p, si, tt, d);
                    d.DependsOn.AddRange(DependsOnColumns(p.TargetLibrary, si.Table, si.Columns.Select(c => c.Name)));
                    Add(d);
                    _indexDiffs[$"{p.TargetLibrary}.{si.Name}"] = d;
                    continue;
                }

                // A target index whose name the source also has is being changed itself, so it cannot stand in for this one.
                var twin = t.Indexes.FirstOrDefault(x => x.Signature == si.Signature && !s.Indexes.Any(y => Eq(y.Name, x.Name)));
                if (twin is not null)
                {
                    matchedTarget.Add(twin);
                    var d = NewDiff(p, ObjectKind.Index, ChangeKind.Drift, si.Name);
                    d.Source = si; d.Target = twin; d.DefaultSelected = false;
                    d.SourceDdl = Ddl.CreateIndex(p.TargetLibrary, si); d.TargetDdl = Ddl.CreateIndex(p.TargetLibrary, twin);
                    d.Details.Add($"Same keys as {twin.Name}");
                    d.Findings.Add(Finding.Of(Severity.Info, "IDX005",
                        $"{I.Target.Key} already has the same index under the name {twin.Name}. Nothing to do unless code refers to the index by name (RPG keyed access to an index does)."));
                    Add(d);
                    continue;
                }

                var add = NewDiff(p, ObjectKind.Index, ChangeKind.Added, si.Name);
                add.Source = si;
                add.SourceDdl = Ddl.CreateIndex(p.TargetLibrary, si);
                add.Details.Add($"{(si.IsUnique ? "UNIQUE " : "")}on {si.Table} ({Keys(si)})");
                add.Findings.Add(Finding.Of(Severity.Info, "IDX001", $"New index {si.Name}" + (tt is { RowCount: > 0 } ? $" on {tt.RowCount:N0} rows." : ".")));
                if (si.IsUnique && tt is { RowCount: > 0 }) UniqueOnExistingRows(p, si, tt, add);
                add.DependsOn.AddRange(DependsOnTable(p, si.Table));
                add.DependsOn.AddRange(DependsOnColumns(p.TargetLibrary, si.Table, si.Columns.Select(c => c.Name)));
                Add(add);
                _indexDiffs[$"{p.TargetLibrary}.{si.Name}"] = add;
            }

            foreach (var ti in t.Indexes.Where(x => !matchedTarget.Contains(x)))
            {
                var d = NewDiff(p, ObjectKind.Index, ChangeKind.Removed, ti.Name);
                d.Target = ti; d.TargetDdl = Ddl.CreateIndex(p.TargetLibrary, ti);
                d.DefaultSelected = false; d.Destructive = true;
                d.Details.Add($"on {ti.Table} ({Keys(ti)})");
                d.Findings.Add(Finding.Of(Severity.Info, "IDX004",
                    $"{ti.Name} exists only in {I.Target.Key}, often a performance index added there. Not dropped unless ticked.",
                    "If it is useful, retrofit it into source so the next promotion does not flag it again."));
                Add(d);
            }
        }

        /// <summary>
        /// A UNIQUE index over rows that already exist. Pre-checks may only touch columns that exist BEFORE the
        /// deployment (they run first), so keys on a column this package adds cannot be checked by query — and on
        /// Db2 for i they do not need to be: every existing row gets NULL (or the same default), and a plain UNIQUE
        /// index treats NULLs as equal, so the CREATE is certain to fail.
        /// </summary>
        private void UniqueOnExistingRows(LibraryPair p, IndexDef si, TableDef tt, Difference d)
        {
            var newCols = si.Columns.Where(c => tt.Column(c.Name) is null).Select(c => c.Name).ToList();
            if (newCols.Count == 0)
            {
                d.Findings.Add(Finding.Of(Severity.Warning, "IDX003", $"Unique index on existing data: duplicates in {I.Target.Key} make CREATE fail (SQL0603).", "A pre-check counts duplicate keys; it must return 0."));
                AddDuplicateCheck(p, si, d);
            }
            else if (!si.UniqueWhereNotNull)
                d.Findings.Add(Finding.Of(Severity.Blocker, "IDX008",
                    $"UNIQUE index on {string.Join(", ", newCols)}, which this package adds to a table with {tt.RowCount:N0} rows. Every existing row starts with the same value (NULL or the default), and Db2 for i treats NULLs as equal in a UNIQUE index, so CREATE fails with SQL0603.",
                    "Define it as CREATE UNIQUE WHERE NOT NULL INDEX, or back-fill distinct values between the column add and the index."));
        }

        private static string Keys(IndexDef i) => string.Join(", ", i.Columns.Select(c => c.Name + (c.Descending ? " DESC" : "")));

        private static void AddDuplicateCheck(LibraryPair p, IndexDef i, Difference d)
        {
            var cols = string.Join(", ", i.Columns.Select(c => Ddl.C(c.Name)));
            var where = i.UniqueWhereNotNull ? " WHERE " + string.Join(" AND ", i.Columns.Select(c => $"{Ddl.C(c.Name)} IS NOT NULL")) : "";
            d.PreChecks.Add(new PreCheck($"No duplicate keys for {i.Name}",
                $"SELECT COUNT(*) FROM (SELECT {cols} FROM {Ddl.Q(p.TargetLibrary, i.Table)}{where} GROUP BY {cols} HAVING COUNT(*) > 1) AS DUPS"));
        }

        // ---------------------------------------------------------------- foreign keys

        private void CompareForeignKeys(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            var matched = new HashSet<ForeignKeyDef>();
            foreach (var sf in s.ForeignKeys)
            {
                var tf = t.ForeignKeys.FirstOrDefault(x => Eq(x.Name, sf.Name));
                var twin = tf is null ? t.ForeignKeys.FirstOrDefault(x => SameKeys(x, sf, p) && !s.ForeignKeys.Any(y => Eq(y.Name, x.Name))) : null;
                if (tf is not null) matched.Add(tf);
                if (twin is not null) matched.Add(twin);

                var refSourceLib = sf.RefLibrary;
                var refTargetLib = I.Map.MapLibrary(refSourceLib);
                var d = NewDiff(p, ObjectKind.ForeignKey, ChangeKind.Added, sf.Name);
                d.Source = sf; d.Target = tf ?? twin;
                d.SourceDdl = Ddl.AddForeignKey(p.TargetLibrary, sf, refTargetLib);
                if ((tf ?? twin) is { } existing) d.TargetDdl = Ddl.AddForeignKey(p.TargetLibrary, existing, existing.RefLibrary);
                d.Details.Add($"{sf.Table}({string.Join(", ", sf.Columns)}) → {refTargetLib}.{sf.RefTable}({string.Join(", ", sf.RefColumns)})");

                // 1. Where does the parent live in the target?
                if (I.Map.ForeignLibraries.TryGetValue(refSourceLib, out var owner))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "FK004", $"Parent table is in {refSourceLib}, which belongs to {owner}. The constraint would tie {I.Target.Key} data to {owner}.", "Point the constraint at the logical library of this environment."));
                else if (!I.Map.IsMapped(refSourceLib) && !Eq(refSourceLib, p.SourceLibrary))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "FK004", $"Parent library {refSourceLib} is not mapped for {I.Target.Key}, so the tool cannot tell which library the parent lives in there.", "Add the library to Turnover:Environments for every environment, or drop the constraint."));

                var parentTarget = TargetLib(refTargetLib)?.Table(sf.RefTable);
                _addedTables.TryGetValue($"{refTargetLib}.{sf.RefTable}", out var parentAdded);
                var parentSource = I.SourceSnapshots.GetValueOrDefault(refSourceLib)?.Table(sf.RefTable);
                var parentInScope = PairedTarget(refTargetLib);

                if (parentTarget is null && parentAdded is null && !d.HasOpenBlocker)
                    d.Findings.Add(Finding.Of(Severity.Blocker, "FK002",
                        $"Parent table {refTargetLib}.{sf.RefTable} does not exist in {I.Target.Key} and is not part of this comparison. ALTER TABLE fails with SQL0204.",
                        $"Include {sf.RefTable}'s library in the comparison, or promote the parent first."));
                if (parentAdded is not null) d.DependsOn.Add(parentAdded.Id);
                d.DependsOn.AddRange(DependsOnTable(p, sf.Table));
                d.DependsOn.AddRange(DependsOnColumns(p.TargetLibrary, sf.Table, sf.Columns));

                // 2. The parent columns must be a primary key / unique key in the target as it will be after deployment.
                var parentAfter = parentInScope || parentTarget is null ? parentSource ?? parentTarget : parentTarget;
                if (parentAfter is not null)
                {
                    var parentLibAfter = parentInScope || parentTarget is null ? refSourceLib : refTargetLib;
                    var indexes = (parentInScope || parentTarget is null ? I.SourceSnapshots.GetValueOrDefault(refSourceLib) : TargetLib(refTargetLib))?.Indexes ?? [];
                    if (!HasUniqueOn(parentAfter, indexes, sf.RefColumns))
                        d.Findings.Add(Finding.Of(Severity.Blocker, "FK003",
                            $"{sf.RefTable}({string.Join(", ", sf.RefColumns)}) is not a primary key, unique constraint or unique index in {(parentLibAfter == refSourceLib ? I.Source.Key : I.Target.Key)}. Db2 rejects the constraint (SQL0573).",
                            "Add the unique key on the parent, or reference its primary key."));
                    else
                    {
                        // Needs a unique index from this package only if neither the target today nor the parent's own
                        // PK / unique constraints cover the referenced columns — and then only the index on exactly those columns.
                        var targetHasIt = parentTarget is not null && HasUniqueOn(parentTarget, TargetLib(refTargetLib)?.Indexes ?? [], sf.RefColumns);
                        if (!targetHasIt && !HasUniqueOn(parentAfter, [], sf.RefColumns))
                            d.DependsOn.AddRange(_indexDiffs.Values.Where(x => x.Change is ChangeKind.Added or ChangeKind.Modified && x.Source is IndexDef ix && ix.IsUnique
                                    && Eq(ix.Table, sf.RefTable) && Eq(x.TargetLibrary, refTargetLib)
                                    && HasUniqueOn(new TableDef { Name = ix.Table }, [ix], sf.RefColumns))
                                .Select(x => x.Id));
                    }
                    if (parentInScope) d.DependsOn.AddRange(DependsOnColumns(refTargetLib, sf.RefTable, sf.RefColumns));

                    // 3. Child and parent column types must match exactly.
                    var child = s.Table(sf.Table);
                    for (var i = 0; i < Math.Min(sf.Columns.Count, sf.RefColumns.Count); i++)
                    {
                        var cc = child?.Column(sf.Columns[i]);
                        var pc = parentAfter.Column(sf.RefColumns[i]);
                        if (cc is not null && pc is not null && cc.TypeSignature != pc.TypeSignature)
                            d.Findings.Add(Finding.Of(Severity.Blocker, "FK006",
                                $"{sf.Table}.{cc.Name} is {cc.TypeSignature} but parent {sf.RefTable}.{pc.Name} is {pc.TypeSignature} in {I.Target.Key}. The constraint cannot be created.",
                                $"Promote the parent column change in {refTargetLib} first, or align the types."));
                    }
                }

                var childTarget = t.Table(sf.Table);
                if (tf is null && twin is null)
                {
                    d.Findings.Insert(0, Finding.Of(Severity.Info, "FK001", $"New foreign key {sf.Name}."));
                    if (childTarget is { RowCount: > 0 }) ExistingRowsCheck(p, sf, refTargetLib, s.Table(sf.Table), childTarget, parentTarget, parentAdded, d);
                    Add(d);
                    continue;
                }

                var existingFk = tf ?? twin!;
                var changes = new List<string>();
                if (!existingFk.Columns.SequenceEqual(sf.Columns, Ci) || !existingFk.RefColumns.SequenceEqual(sf.RefColumns, Ci) || !Eq(existingFk.RefTable, sf.RefTable))
                    changes.Add("key columns");
                if (!Eq(existingFk.DeleteRule, sf.DeleteRule)) changes.Add($"ON DELETE {existingFk.DeleteRule} → {sf.DeleteRule}");
                if (!Eq(existingFk.UpdateRule, sf.UpdateRule)) changes.Add($"ON UPDATE {existingFk.UpdateRule} → {sf.UpdateRule}");
                if (!Eq(existingFk.RefLibrary, refTargetLib) && changes.Count == 0)
                    changes.Add($"points at {existingFk.RefLibrary} instead of {refTargetLib}");

                if (changes.Count > 0)
                {
                    d.Change = ChangeKind.Modified;
                    d.Details.AddRange(changes);
                    var cascade = Eq(sf.DeleteRule, "CASCADE") && !Eq(existingFk.DeleteRule, "CASCADE");
                    d.Findings.Add(Finding.Of(cascade ? Severity.Warning : Severity.Info, "FK005",
                        $"Constraint differs ({string.Join("; ", changes)}). Script drops and re-adds it." +
                        (cascade ? $" ON DELETE CASCADE means a delete of a {sf.RefTable} row in {I.Target.Key} now silently deletes {sf.Table} rows." : "")));
                    if (childTarget is { RowCount: > 0 }) ExistingRowsCheck(p, sf, refTargetLib, s.Table(sf.Table), childTarget, parentTarget, parentAdded, d);
                    Add(d);
                }
                else if (twin is not null)
                {
                    d.Change = ChangeKind.Drift; d.DefaultSelected = false;
                    d.Details.Add($"Same keys as {twin.Name}");
                    d.Findings.Add(Finding.Of(Severity.Info, "FK009", $"{I.Target.Key} has the same constraint named {twin.Name}. Nothing to do."));
                    Add(d);
                }
                else if (d.HasOpenBlocker)
                {
                    d.Change = ChangeKind.Drift;
                    Add(d);
                }
            }

            foreach (var tf in t.ForeignKeys.Where(x => !matched.Contains(x)))
            {
                var d = NewDiff(p, ObjectKind.ForeignKey, ChangeKind.Removed, tf.Name);
                d.Target = tf; d.TargetDdl = Ddl.AddForeignKey(p.TargetLibrary, tf, tf.RefLibrary);
                d.DefaultSelected = false; d.Destructive = true;
                d.Details.Add($"{tf.Table}({string.Join(", ", tf.Columns)}) → {tf.RefLibrary}.{tf.RefTable}");
                d.Findings.Add(Finding.Of(Severity.Warning, "FK007",
                    $"{tf.Name} exists only in {I.Target.Key}. Inserts and deletes that work in {I.Source.Key} can fail there (SQL0530 / SQL0532), so testing in {I.Source.Key} does not prove the change works in {I.Target.Key}.",
                    $"Retrofit the constraint into {I.Source.Key}, or confirm it should be dropped."));
                Add(d);
            }
        }

        private bool SameKeys(ForeignKeyDef target, ForeignKeyDef source, LibraryPair p) =>
            Eq(target.Table, source.Table) && Eq(target.RefTable, source.RefTable) &&
            target.Columns.SequenceEqual(source.Columns, Ci) && target.RefColumns.SequenceEqual(source.RefColumns, Ci) &&
            Eq(target.RefLibrary, I.Map.MapLibrary(source.RefLibrary));

        internal static bool HasUniqueOn(TableDef table, IEnumerable<IndexDef> indexes, IReadOnlyCollection<string> columns)
        {
            bool Same(IEnumerable<string> a) => a.Count() == columns.Count && !a.Except(columns, Ci).Any();
            return Same(table.PrimaryKey) || table.UniqueKeys.Any(Same) ||
                   indexes.Any(i => i.IsUnique && Eq(i.Table, table.Name) && Same(i.Columns.Select(c => c.Name)));
        }

        /// <summary>
        /// Rows already in the child table must all have a parent once the constraint is added. Pre-checks run
        /// before anything is deployed, so they may only reference columns and tables that exist in the target now.
        /// </summary>
        private void ExistingRowsCheck(LibraryPair p, ForeignKeyDef sf, string refTargetLib, TableDef? childSource, TableDef childTarget,
            TableDef? parentTarget, Difference? parentAdded, Difference d)
        {
            var missing = sf.Columns.Where(c => childTarget.Column(c) is null).ToList();
            if (missing.Count > 0)
            {
                // New key columns: existing rows get NULL (never an orphan) or the column default (an orphan unless the parent has it).
                var defaulted = missing.Select(c => childSource?.Column(c)).Where(c => c is null || !c.Nullable || c.Default is not null).ToList();
                if (defaulted.Count > 0)
                    d.Findings.Add(Finding.Of(Severity.Warning, "FK010",
                        $"{string.Join(", ", missing)} is added with a default, so all {childTarget.RowCount:N0} existing rows get the same key. The constraint fails unless {sf.RefTable} has that row.",
                        "Insert the parent row first, or add the column nullable and back-fill."));
                return;
            }
            if (parentTarget is null)
            {
                if (parentAdded is null) return; // FK002 already reported
                d.Findings.Add(Finding.Of(Severity.Warning, "FK008",
                    $"The parent {sf.RefTable} is new (empty) but {sf.Table} already has {childTarget.RowCount:N0} rows in {I.Target.Key}; every non-null key would be an orphan.",
                    "Load the parent rows before this step. The pre-check stops the deployment otherwise."));
                d.PreChecks.Add(new PreCheck($"No existing keys for new parent {sf.RefTable}",
                    $"SELECT COUNT(*) FROM {Ddl.Q(p.TargetLibrary, sf.Table)} WHERE {string.Join(" OR ", sf.Columns.Select(c => $"{Ddl.C(c)} IS NOT NULL"))}"));
                return;
            }
            d.PreChecks.Add(OrphanCheck(p, sf, refTargetLib));
        }

        private static PreCheck OrphanCheck(LibraryPair p, ForeignKeyDef fk, string refLib)
        {
            var notNull = string.Join(" AND ", fk.Columns.Select(c => $"C.{Ddl.C(c)} IS NOT NULL"));
            var join = string.Join(" AND ", fk.Columns.Zip(fk.RefColumns, (c, r) => $"P.{Ddl.C(r)} = C.{Ddl.C(c)}"));
            return new PreCheck($"No orphan rows for {fk.Name}",
                $"SELECT COUNT(*) FROM {Ddl.Q(p.TargetLibrary, fk.Table)} C WHERE {notNull} AND NOT EXISTS (SELECT 1 FROM {Ddl.Q(refLib, fk.RefTable)} P WHERE {join})");
        }

        // ---------------------------------------------------------------- RPG / CL programs

        private void ComparePrograms(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sp in s.Programs)
            {
                var tp = t.Programs.FirstOrDefault(x => Eq(x.Name, sp.Name) && Eq(x.ObjectType, sp.ObjectType));
                var kind = KindOf(sp);
                var d = NewDiff(p, kind, tp is null ? ChangeKind.Added : ChangeKind.Modified, sp.Name);
                d.Source = sp; d.Target = tp;
                d.SourceDdl = Ddl.Describe(sp, p.SourceLibrary);
                if (tp is not null) d.TargetDdl = Ddl.Describe(tp, p.TargetLibrary);

                if (tp is null)
                    d.Findings.Add(Finding.Of(Severity.Info, "PGM000", $"New {sp.ObjectType} {sp.Attribute}. Source member is copied and compiled in {p.TargetLibrary}."));
                else if (!Eq(sp.SourceHash, tp.SourceHash))
                {
                    d.Details.Add($"Source changed {tp.SourceChanged:yyyy-MM-dd} → {sp.SourceChanged:yyyy-MM-dd}");
                    d.Findings.Add(Finding.Of(Severity.Info, "PGM001", "Source changed: member is copied and the object recompiled."));
                    if (tp.SourceChanged > sp.SourceChanged)
                        d.Findings.Add(Finding.Of(Severity.Blocker, "PGM005",
                            $"The {I.Target.Key} source was changed on {tp.SourceChanged:yyyy-MM-dd HH:mm}, after the {I.Source.Key} source ({sp.SourceChanged:yyyy-MM-dd HH:mm}). That is usually an emergency fix made directly in {I.Target.Key}; deploying would overwrite it.",
                            $"Retrofit the {I.Target.Key} fix into {I.Source.Key}, re-test, then promote."));
                }
                else continue;

                if (sp.SourceHash == ProgramObject.MissingSource)
                    d.Findings.Add(Finding.Of(Severity.Blocker, "PGM017", $"The source member {sp.SourceLibrary}/{sp.SourceFile}({sp.SourceMember}) that {sp.Name} was compiled from does not exist in {I.Source.Key}.",
                        "Restore the member or recompile the object from the source that is really in use."));
                if (sp.Created < sp.SourceChanged)
                    d.Findings.Add(Finding.Of(Severity.Warning, "PGM002",
                        $"In {I.Source.Key} the object was compiled {sp.Created:yyyy-MM-dd HH:mm}, before its source last changed ({sp.SourceChanged:yyyy-MM-dd HH:mm}). What was tested is not what will be compiled.",
                        $"Recompile in {I.Source.Key} and re-test before promoting."));

                CompileOptionFindings(sp, tp, d);
                if (sp.ModuleCount > 1)
                    d.Findings.Add(Finding.Of(Severity.Warning, "PGM012", $"{sp.Name} is bound from {sp.ModuleCount} modules; one compile command cannot rebuild it. The plan lists it as a manual build."));
                else if (string.IsNullOrEmpty(sp.SourceFile) || string.IsNullOrEmpty(sp.SourceLibrary))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "PGM011", $"{sp.Name} has no source recorded in {I.Source.Key}; it cannot be rebuilt from source.", "Recompile it from its source member so the object records where its source is."));
                else if (!Ddl.CanBuild(sp))
                    d.Findings.Add(Finding.Of(Severity.Warning, "PGM013", $"No one-command build for {sp.ObjectType} {sp.Attribute}. The plan lists it as a manual build."));
                if (!string.IsNullOrEmpty(sp.SourceLibrary) && !I.Map.IsMapped(sp.SourceLibrary))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "PGM008", $"Source library {sp.SourceLibrary} is not mapped for {I.Target.Key}, so the tool does not know where to copy the source.", "Map the source library as a logical library (e.g. ORDSRC)."));

                foreach (var bound in sp.BoundServicePrograms)
                {
                    var tgtName = I.Map.MapQualifiedObject(bound);
                    var parts = tgtName.Split('/');
                    var exists = parts.Length == 2 && (TargetLib(parts[0])?.Programs.Any(x => Eq(x.Name, parts[1]) && Eq(x.ObjectType, "*SRVPGM")) ?? false);
                    var inScope = parts.Length == 2 && s.Programs.Concat(I.SourceSnapshots.Values.SelectMany(x => x.Programs)).Any(x => Eq(x.Name, parts[1]) && Eq(x.ObjectType, "*SRVPGM")) && PairedTarget(parts[0]);
                    if (I.Map.ForeignLibraries.ContainsKey(bound.Split('/')[0]))
                        d.Findings.Add(Finding.Of(Severity.Blocker, "PGM003", $"Bound to {bound}, which belongs to another environment."));
                    else if (!exists && !inScope)
                        d.Findings.Add(Finding.Of(Severity.Blocker, "PGM003", $"Bound service program {tgtName} does not exist in {I.Target.Key}. The compile fails, or the call fails with MCH3401.", "Promote the service program first or include its library."));
                }

                foreach (var fr in sp.FileReferences.Where(f => !f.Library.StartsWith('*')))
                {
                    if (I.Map.ForeignLibraries.TryGetValue(fr.Library, out var owner))
                        d.Findings.Add(Finding.Of(Severity.Blocker, "PGM010", $"Opens {fr.Library}/{fr.File}, which belongs to {owner}. After promotion it would still use {owner} data."));
                    else if (I.Map.IsMapped(fr.Library) && !Eq(fr.Library, I.Map.MapLibrary(fr.Library)))
                        d.Findings.Add(Finding.Of(Severity.Warning, "PGM010",
                            $"Opens {fr.Library}/{fr.File} with a hard-coded library (EXTFILE / qualified SQL). The source is copied as-is, so in {I.Target.Key} it still points at {fr.Library}.",
                            "Use *LIBL (unqualified) names, or EXTFILE(*EXTDESC) with an override."));
                }

                if (kind == ObjectKind.ServiceProgram && tp is not null)
                {
                    var callerObjs = I.TargetSnapshots.Values.SelectMany(x => x.Programs.Select(pg => (Lib: x.Library, Pg: pg)))
                        .Where(x => x.Pg.BoundServicePrograms.Any(b => Eq(b.Split('/').Last(), sp.Name))).ToList();
                    var callers = callerObjs.Select(x => $"{x.Lib}/{x.Pg.Name}").ToList();
                    foreach (var c in callerObjs)
                        d.FollowUps.Add($"UPDPGM PGM({c.Lib}/{c.Pg.Name}) MODULE(*NONE) BNDSRVPGM(({p.TargetLibrary}/{sp.Name}))");
                    if (callers.Count > 0)
                        d.Findings.Add(Finding.Of(Severity.Warning, "PGM006",
                            $"Service program is re-created. If its export list (binder source) changed, the signature changes and {Summ(callers)} fail with CPF5D04 / MCH4431 until re-bound.",
                            "Script re-binds callers with UPDPGM after the compile.", autoFixed: true));
                }

                if (kind == ObjectKind.DdsFile)
                {
                    DdsFindings(p, sp, tp, d);
                    // A changed display / printer / logical file has a new record format: programs using it need a recompile.
                    if (tp is not null) MarkFormatChangeKey($"{p.TargetLibrary}/{sp.Name}", d);
                }
                if (kind == ObjectKind.Command)
                    d.Findings.Add(Finding.Of(Severity.Info, "CMD001", $"Command is re-created with CRTCMD; processing program {I.Map.MapQualifiedObject(sp.CommandProgram ?? "*LIBL/" + sp.Name)}."));
                if (tp is not null && kind is ObjectKind.DdsFile or ObjectKind.DataQueue) PreserveAuthorities(t, d, sp.Name, sp.ObjectType);
                Add(d);
                _programDiffs[$"{p.TargetLibrary}/{sp.Name}/{sp.ObjectType}"] = d;
            }

            foreach (var tp in t.Programs.Where(x => !s.Programs.Any(y => Eq(y.Name, x.Name) && Eq(y.ObjectType, x.ObjectType))))
            {
                var d = NewDiff(p, KindOf(tp), ChangeKind.Removed, tp.Name);
                d.Target = tp; d.TargetDdl = Ddl.Describe(tp, p.TargetLibrary);
                d.DefaultSelected = false;
                d.Findings.Add(Finding.Of(Severity.Info, "PGM009", $"{tp.Name} exists only in {I.Target.Key}. Programs are never deleted by a turnover."));
                Add(d);
            }
        }

        /// <summary>
        /// Every program in the TARGET that opens a file whose record format changes must be recompiled,
        /// or it ends with CPF4131 (level check) the first time it runs. This is the most common IBM i
        /// turnover failure, and it hits programs in libraries nobody thought were part of the change.
        /// </summary>
        private void CheckLevelChecks()
        {
            var tableLibByName = I.TargetSnapshots.Values
                .SelectMany(s => s.Tables.Select(t => (s.Library, t.SystemName)))
                .GroupBy(x => x.SystemName, Ci).ToDictionary(g => g.Key, g => g.First().Library, Ci);
            var libOrder = I.Target.Libraries.Values.ToList();
            // Files per library, built once: scanning every library's object lists per file reference was quadratic.
            var filesByLib = libOrder.Distinct(Ci).ToDictionary(l => l, l => TargetLib(l) is { } tl
                ? tl.Tables.Select(t => t.SystemName).Concat(tl.Programs.Where(x => Eq(x.ObjectType, "*FILE")).Select(x => x.Name)).ToHashSet(Ci)
                : new HashSet<string>(Ci), Ci);

            foreach (var snap in I.TargetSnapshots.Values.OrderBy(x => libOrder.IndexOf(x.Library)))
                foreach (var pg in snap.Programs)
                {
                    var hits = new List<string>();
                    var causes = new List<Difference>();
                    foreach (var fr in pg.FileReferences)
                    {
                        var lib = fr.Library.StartsWith('*')
                            ? libOrder.FirstOrDefault(l => filesByLib[l].Contains(fr.File))
                              ?? tableLibByName.GetValueOrDefault(fr.File)
                            : fr.Library;
                        if (lib is null) continue;
                        if (_formatChanged.TryGetValue($"{lib}/{fr.File}", out var why)) { hits.Add($"{lib}/{fr.File}"); causes.AddRange(why); }
                    }
                    if (hits.Count == 0) continue;

                    var logical = I.Target.LogicalFor(snap.Library) ?? snap.Library;
                    var kind = KindOf(pg);
                    var id = Difference.MakeId(kind, logical, pg.Name);
                    var finding = Finding.Of(Severity.Warning, "PGM004",
                        $"Opens {Summ(hits)}, whose record format changes in this package. Without a recompile it ends with CPF4131 (level check) the first time it runs.",
                        $"Script recompiles it from the {I.Target.Key} source after the table changes.", autoFixed: true);

                    // Match by library + name, not id: in library mode the program diff carries another logical name, and a
                    // second "recompile from target source" entry would overwrite the new program with the old source.
                    if (_programDiffs.TryGetValue($"{snap.Library}/{pg.Name}/{pg.ObjectType}", out var already))
                    {
                        already.Findings.Add(Finding.Of(Severity.Info, "PGM004", $"Also picks up the new record format of {Summ(hits)}."));
                        continue;
                    }
                    var pair = I.Pairs.FirstOrDefault(x => Eq(x.TargetLibrary, snap.Library)) ?? new LibraryPair(logical, I.Source.PhysicalFor(logical) ?? snap.Library, snap.Library);
                    var d = NewDiff(pair, kind, ChangeKind.Drift, pg.Name);
                    d.Source = pg; d.Target = pg;
                    d.TargetDdl = Ddl.Describe(pg, snap.Library);
                    d.Details.Add("Recompile only (level check)");
                    d.Findings.Add(finding);
                    // Only needed if the table change ships; listed as a dependency so the planner can drop it if not.
                    d.DependsOn.AddRange(causes.Select(c => c.Id).Distinct());
                    Add(d);
                }
        }

        /// <summary>
        /// Renaming or dropping a column breaks every procedure, view and trigger in the TARGET that still uses the
        /// old name. Those objects must be replaced in the same package (dependency), or — if they exist only in the
        /// target — the change is blocked.
        /// </summary>
        private void CheckDroppedColumnUsers()
        {
            var dropped = _diffs.Where(d => d.Kind == ObjectKind.Column && d.Change is ChangeKind.Renamed or ChangeKind.Removed).ToList();
            if (dropped.Count == 0) return;
            // Tokenise every target body once; each check below is then a set lookup.
            var bodies = I.TargetSnapshots.Values.Select(snap => (snap, Users: snap.Routines.Select(r => (Kind: Eq(r.Kind, "FUNCTION") ? ObjectKind.Function : ObjectKind.Procedure, r.Name, Ids: SqlText.Identifiers(r.Definition)))
                    .Concat(snap.Views.Select(v => (Kind: ObjectKind.View, v.Name, Ids: SqlText.Identifiers(v.Definition))))
                    .Concat(snap.Triggers.Select(t => (Kind: ObjectKind.Trigger, t.Name, Ids: SqlText.Identifiers(t.Definition))))
                    .ToList()))
                .ToList();
            foreach (var col in dropped)
            {
                var old = ((ColumnDef)col.Target!).Name;
                foreach (var (snap, all) in bodies)
                {
                    var users = all.Where(u => u.Ids.Contains(col.Parent!) && u.Ids.Contains(old));
                    foreach (var u in users)
                    {
                        var logical = I.Target.LogicalFor(snap.Library) ?? snap.Library;
                        // By kind + logical + name (overloads carry the specific name in their id).
                        // Only a REPLACEMENT in this package fixes the user; a target-only "Removed" entry for the same object
                        // must not count (ticking it would DROP the view instead of fixing it).
                        var id = _diffs.FirstOrDefault(x => x.Kind == u.Kind && x.Change != ChangeKind.Removed && Eq(x.Logical, logical) && Eq(x.Name, u.Name))?.Id;
                        if (id is not null)
                        {
                            col.DependsOn.Add(id);
                            col.Details.Add($"{u.Name} must ship too (uses {old})");
                        }
                        else
                            col.Findings.Add(Finding.Of(Severity.Blocker, "COL014",
                                $"{snap.Library}.{u.Name} in {I.Target.Key} uses {col.Parent}.{old} and has no replacement in {I.Source.Key}. {(col.Change == ChangeKind.Renamed ? "Renaming" : "Dropping")} the column breaks it (SQL0206 at run time).",
                                $"Update {u.Name} in {I.Source.Key} first, or retrofit it if it exists only in {I.Target.Key}."));
                    }
                }
            }
        }

        // ---------------------------------------------------------------- routines, views, triggers

        private void CompareRoutines(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            var replaced = new HashSet<string>(Ci);   // target overloads a source routine replaces (RTN005)
            foreach (var sr in s.Routines)
            {
                var kind = Eq(sr.Kind, "FUNCTION") ? ObjectKind.Function : ObjectKind.Procedure;
                var tr = t.Routines.FirstOrDefault(x => Eq(x.SpecificName, sr.SpecificName));
                var remap = I.Map.Remap(sr.Definition);
                if (I.Mode == CompareMode.Library && !string.IsNullOrEmpty(I.ProgramLibrary))
                    remap = remap with { Text = System.Text.RegularExpressions.Regex.Replace(remap.Text, $@"(EXTERNAL\s+NAME\s+'){Regex.Escape(p.TargetLibrary)}/", $"$1{I.ProgramLibrary}/", RegexOptions.IgnoreCase) };
                var d = NewDiff(p, kind, ChangeKind.Added, sr.Name);
                // Overloads share a name: give each its own id, or Add() would merge them and one would never ship.
                var overloaded = s.Routines.Count(x => Eq(x.Name, sr.Name) && Eq(x.Kind, sr.Kind)) > 1;
                if (overloaded) d.Id = Difference.MakeId(kind, p.Logical, $"{sr.Name}({sr.SpecificName})");
                d.Source = sr;
                d.SourceDdl = remap.Text;

                if (tr is null)
                {
                    // One-for-one replacement only: with several source overloads there is no telling which old one each replaces.
                    var overloads = overloaded ? [] : t.Routines.Where(x => Eq(x.Name, sr.Name) && Eq(x.Kind, sr.Kind) && !s.Routines.Any(y => Eq(y.SpecificName, x.SpecificName))).ToList();
                    if (overloads.Count > 0)
                    {
                        var old = overloads[0];
                        replaced.Add(old.SpecificName);
                        d.Change = ChangeKind.Modified; d.Target = old; d.TargetDdl = old.Definition; d.Destructive = true;
                        d.Details.Add($"Parameters {old.Parameters.Count} → {sr.Parameters.Count}; specific name {old.SpecificName} → {sr.SpecificName}");
                        d.Findings.Add(Finding.Of(Severity.Warning, "RTN005",
                            $"The parameter list changed. Db2 for i overloads {sr.Kind.ToLowerInvariant()}s by number of parameters, so CREATE OR REPLACE adds a second version and leaves {old.SpecificName} in place: existing CALLs keep running the old code.",
                            $"Script drops SPECIFIC {old.SpecificName} first. Update every caller in the same package.", autoFixed: true));
                    }
                    else d.Findings.Add(Finding.Of(Severity.Info, "RTN001", $"New {sr.Kind.ToLowerInvariant()} {sr.Name}."));
                }
                else
                {
                    d.Target = tr; d.TargetDdl = tr.Definition;
                    if (LibraryMap.Normalize(remap.Text) == LibraryMap.Normalize(tr.Definition)) continue;
                    d.Change = ChangeKind.Modified;
                    d.Findings.Add(Finding.Of(Severity.Info, "RTN001", "Definition changed."));
                }

                LibraryRewriteFindings(d, remap, sr.Name, sr.Name, sr.SpecificName);
                CheckExternalName(p, sr, d);
                d.DependsOn.AddRange(DependsOnMentioned(remap.Text, p.TargetLibrary));
                Add(d);
            }

            foreach (var tr in t.Routines.Where(x => !s.Routines.Any(y => Eq(y.SpecificName, x.SpecificName)) && !replaced.Contains(x.SpecificName)))
            {
                var rk = Eq(tr.Kind, "FUNCTION") ? ObjectKind.Function : ObjectKind.Procedure;
                var d = NewDiff(p, rk, ChangeKind.Removed, tr.Name);
                // A target-only overload of a routine the source also has: its own id, so it does not merge with the source one.
                if (s.Routines.Any(y => Eq(y.Name, tr.Name)) || t.Routines.Count(y => Eq(y.Name, tr.Name)) > 1)
                    d.Id = Difference.MakeId(rk, p.Logical, $"{tr.Name}({tr.SpecificName})");
                d.Target = tr; d.TargetDdl = tr.Definition; d.DefaultSelected = false; d.Destructive = true;
                d.Findings.Add(Finding.Of(Severity.Info, "RTN008", $"{tr.Name} exists only in {I.Target.Key}. Not dropped unless ticked."));
                Add(d);
            }
        }

        private void CheckExternalName(LibraryPair p, RoutineDef sr, Difference d)
        {
            if (string.IsNullOrWhiteSpace(sr.ExternalName)) return;
            var ext = sr.ExternalName.Trim('\'', ' ');
            var slash = ext.IndexOf('/');
            if (slash < 0)
            {
                d.Findings.Add(Finding.Of(Severity.Info, "RTN009", $"EXTERNAL NAME {ext} is unqualified: resolved through the library list when the routine is created."));
                return;
            }
            var lib = ext[..slash];
            var obj = ext[(slash + 1)..].Split('(')[0];
            if (lib.StartsWith('*')) return;
            if (I.Map.ForeignLibraries.TryGetValue(lib, out var owner))
            {
                d.Findings.Add(Finding.Of(Severity.Blocker, "RTN006", $"EXTERNAL NAME {ext} points at {owner}'s library {lib}."));
                return;
            }
            if (!I.Map.IsMapped(lib))
            {
                d.Findings.Add(Finding.Of(Severity.Blocker, "RTN006", $"EXTERNAL NAME library {lib} is not mapped for {I.Target.Key}.", "Map the program library as a logical library."));
                return;
            }
            var tgtLib = I.Map.MapLibrary(lib);
            if (!Eq(lib, tgtLib))
                d.Details.Add($"EXTERNAL NAME {lib}/{obj} → {tgtLib}/{obj}");
            if (_programDiffs.TryGetValue($"{tgtLib}/{obj}/*PGM", out var pgm) || _programDiffs.TryGetValue($"{tgtLib}/{obj}/*SRVPGM", out pgm)) { d.DependsOn.Add(pgm.Id); return; }
            if (TargetLib(tgtLib)?.Programs.Any(x => Eq(x.Name, obj)) ?? false) return;
            d.Findings.Add(Finding.Of(Severity.Blocker, "RTN004",
                $"EXTERNAL NAME {tgtLib}/{obj} does not exist in {I.Target.Key} and is not part of this comparison. CALL fails with SQL0204 / MCH3401.",
                $"Include the program library ({tgtLib}) in the comparison, or promote the program first."));
        }

        private void CompareViews(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sv in s.Views)
            {
                var tv = t.Views.FirstOrDefault(x => Eq(x.Name, sv.Name));
                var remap = I.Map.Remap(sv.Definition);
                var refs = sv.References.Select(r => I.Map.Remap(r).Text).ToList();
                var d = NewDiff(p, ObjectKind.View, tv is null ? ChangeKind.Added : ChangeKind.Modified, sv.Name);
                d.Source = sv; d.Target = tv; d.SourceDdl = remap.Text; d.TargetDdl = tv?.Definition;

                var changed = tv is null || LibraryMap.Normalize(remap.Text) != LibraryMap.Normalize(tv.Definition);
                var starOverChanged = !changed && Regex.IsMatch(sv.Definition, @"SELECT\s+(\w+\.)?\*", RegexOptions.IgnoreCase) &&
                                      refs.Any(r => _formatChanged.Keys.Any(k => FormatKeyMatches(k, r)));
                if (!changed && !starOverChanged) continue;

                d.Findings.Add(tv is null
                    ? Finding.Of(Severity.Info, "VIEW001", $"New view {sv.Name}.")
                    : changed ? Finding.Of(Severity.Info, "VIEW001", "View definition changed.")
                    : Finding.Of(Severity.Warning, "VIEW002", "SELECT * view over a table whose columns change. Db2 fixes the column list when the view is created, so it is re-created to pick up the change.", autoFixed: true));

                foreach (var r in refs)
                {
                    var parts = r.Split('.');
                    if (parts.Length != 2) continue;
                    _addedTables.TryGetValue(r, out var tbl);
                    if (TargetLib(parts[0])?.Table(parts[1]) is null && tbl is null && !PairedTarget(parts[0]))
                        d.Findings.Add(Finding.Of(Severity.Blocker, "VIEW003", $"Reads {r}, which does not exist in {I.Target.Key}."));
                }
                d.DependsOn.AddRange(DependsOnMentioned(remap.Text, p.TargetLibrary));
                LibraryRewriteFindings(d, remap, sv.Name, sv.Name);
                Add(d);
            }
            foreach (var tv in t.Views.Where(x => !s.Views.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.View, tv.Name, tv, tv.Definition, "VIEW004"));
        }

        private bool FormatKeyMatches(string formatKey, string libDotTable)
        {
            var parts = libDotTable.Split('.');
            if (parts.Length != 2) return false;
            var sys = TargetLib(parts[0])?.Table(parts[1])?.SystemName ?? parts[1];
            return Eq(formatKey, $"{parts[0]}/{sys}");
        }

        private void CompareTriggers(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var st in s.Triggers)
            {
                var tt = t.Triggers.FirstOrDefault(x => Eq(x.Name, st.Name));
                var remap = I.Map.Remap(st.Definition);
                if (tt is not null && LibraryMap.Normalize(remap.Text) == LibraryMap.Normalize(tt.Definition)) continue;
                var d = NewDiff(p, ObjectKind.Trigger, tt is null ? ChangeKind.Added : ChangeKind.Modified, st.Name);
                d.Source = st; d.Target = tt; d.SourceDdl = remap.Text; d.TargetDdl = tt?.Definition;
                d.Findings.Add(Finding.Of(Severity.Info, "TRG001", tt is null ? $"New trigger on {st.Table}." : "Trigger definition changed."));
                if (t.Table(st.Table) is { RowCount: > 0 } tbl)
                    d.Findings.Add(Finding.Of(Severity.Info, "TRG002", $"Creating a trigger needs an exclusive lock on {st.Table} ({tbl.RowCount:N0} rows); jobs holding it open make the step wait or fail."));
                d.DependsOn.AddRange(DependsOnTable(p, st.Table));
                d.DependsOn.AddRange(DependsOnMentioned(remap.Text, p.TargetLibrary));
                LibraryRewriteFindings(d, remap, st.Name, st.Name);
                Add(d);
            }
            // A trigger only in the target (an audit trigger added in production, say) was never tested below it.
            foreach (var tt in t.Triggers.Where(x => !s.Triggers.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.Trigger, tt.Name, tt, tt.Definition, "TRG003"));
        }
    }
}
