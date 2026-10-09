using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

public sealed partial class SchemaComparer
{
    private sealed partial class Run
    {
        internal static ObjectKind KindOf(ProgramObject p) => p.ObjectType.ToUpperInvariant() switch
        {
            "*SRVPGM" => ObjectKind.ServiceProgram,
            "*FILE" => ObjectKind.DdsFile,
            "*CMD" => ObjectKind.Command,
            "*MENU" or "*PNLGRP" => ObjectKind.Menu,
            _ => ObjectKind.Program,
        };

        private static readonly string[] Powerful = ["*ALLOBJ", "*SECADM", "*AUDIT", "*IOSYSCFG", "*SERVICE", "*SPLCTL", "*JOBCTL", "*SAVSYS"];
        private static readonly string[] ClassRank = ["*USER", "*SYSOPR", "*PGMR", "*SECADM", "*SECOFR"];
        private static readonly string[] AuthorityRank = ["*EXCLUDE", "*USE", "*CHANGE", "*ALL"];

        // ------------------------------------------------------------ compile options

        /// <summary>
        /// The rebuild replays the SOURCE object's options. Differences from the target's current object are shown,
        /// and the ones that change behaviour or security are flagged.
        /// </summary>
        private void CompileOptionFindings(ProgramObject sp, ProgramObject? tp, Difference d)
        {
            if (sp.ObjectType is not ("*PGM" or "*SRVPGM")) return;
            if (sp.Options is null)
            {
                d.Findings.Add(Finding.Of(Severity.Info, "PGM015", "Compile options were not recorded for this object; the rebuild uses the options in the source (H-spec / ctl-opt) and command defaults."));
                return;
            }
            if (sp.ObjectType == "*SRVPGM" && sp.Options.ExportSourceFile is null)
                d.Findings.Add(Finding.Of(Severity.Warning, "PGM016", "No binder source is recorded, so the service program is rebuilt with EXPORT(*ALL): its signature can change and callers must be re-bound.",
                    "Create it from binder source (EXPORT(*SRCFILE)) in the source environment."));
            var t = tp?.Options;
            if (t is null) return;
            var changed = new List<string>();
            void Cmp(string what, string? before, string? after) { if (!string.Equals(before ?? "", after ?? "", StringComparison.OrdinalIgnoreCase)) changed.Add($"{what} {before ?? "—"} → {after ?? "—"}"); }
            Cmp("ACTGRP", t.ActivationGroup, sp.Options.ActivationGroup);
            Cmp("TGTRLS", t.TargetRelease, sp.Options.TargetRelease);
            Cmp("USRPRF", t.UserProfile, sp.Options.UserProfile);
            Cmp("COMMIT", t.SqlCommitment, sp.Options.SqlCommitment);
            Cmp("NAMING", t.SqlNaming, sp.Options.SqlNaming);
            Cmp("OPTIMIZE", t.OptimizationLevel?.ToString(), sp.Options.OptimizationLevel?.ToString());
            if (changed.Count == 0) return;
            d.Details.Add("Compile options: " + string.Join(", ", changed));
            var adopts = Eq(sp.Options.UserProfile, "*OWNER") && !Eq(t.UserProfile, "*OWNER");
            d.Findings.Add(Finding.Of(adopts ? Severity.Warning : Severity.Info, "PGM014",
                adopts ? $"{sp.Name} will run with its OWNER's authority (USRPRF *OWNER) in {I.Target.Key}; today it runs with the caller's. Every user who can call it gets the owner's access."
                       : $"Compile options differ from the object in {I.Target.Key}: {string.Join(", ", changed)}. The rebuild uses the {I.Source.Key} options.",
                adopts ? "Confirm with security that adopted authority is intended." : null));
        }

        // ------------------------------------------------------------ DDS files (DSPF / PRTF / LF / DDS PF)

        private void DdsFindings(LibraryPair p, ProgramObject sp, ProgramObject? tp, Difference d)
        {
            if (tp is null) return;
            switch (sp.Attribute.ToUpperInvariant())
            {
                case "PF":
                    var rows = TargetLib(p.TargetLibrary)?.Table(sp.Name)?.RowCount;
                    d.Findings.Add(Finding.Of(Severity.Warning, "DDS001",
                        $"DDS physical file changed. CHGPF keeps the data but rebuilds the file{(rows is > 0 ? $" ({rows:N0} rows)" : "")} under an exclusive lock.",
                        "Deploy when no job has the file open."));
                    break;
                case "DSPF" or "PRTF" or "LF":
                    d.Findings.Add(Finding.Of(Severity.Info, "DDS002",
                        $"{sp.Attribute} is deleted and re-created from source; its private authorities are re-applied afterwards. Programs that use it are recompiled (level check)."));
                    break;
            }
        }

        /// <summary>Objects that are deleted and re-created lose private authorities; re-grant what the target has today.</summary>
        private static void PreserveAuthorities(SchemaSnapshot target, Difference d, string obj, string type)
        {
            foreach (var a in target.Authorities.Where(x => Eq(x.Object, obj) && Eq(x.ObjectType, type)))
            {
                if (a.User == "*OWNER") d.FollowUps.Add($"CHGOBJOWN OBJ({d.TargetLibrary}/{obj}) OBJTYPE({type}) NEWOWN({a.Authority}) CUROWNAUT(*SAME)");
                else if (AuthorityRank.Contains(a.Authority, Ci))
                    d.FollowUps.Add($"GRTOBJAUT OBJ({d.TargetLibrary}/{obj}) OBJTYPE({type}) USER({a.User}) AUT({a.Authority}) REPLACE(*YES)");
            }
        }

        // ------------------------------------------------------------ check constraints

        private void CompareChecks(LibraryPair p, TableDef st, TableDef tt)
        {
            foreach (var sc in st.Checks)
            {
                var tc = tt.Checks.FirstOrDefault(x => Eq(x.Name, sc.Name));
                var clause = I.Map.Remap(sc.Clause).Text;
                if (tc is not null && LibraryMap.Normalize(clause) == LibraryMap.Normalize(tc.Clause)) continue;
                var d = NewDiff(p, ObjectKind.CheckConstraint, tc is null ? ChangeKind.Added : ChangeKind.Modified, sc.Name, st.Name);
                d.Source = sc with { Clause = clause }; d.Target = tc;
                d.SourceDdl = $"CHECK ({clause})"; d.TargetDdl = tc is null ? null : $"CHECK ({tc.Clause})";
                d.Findings.Add(Finding.Of(Severity.Info, "CHK001", tc is null ? $"New check constraint on {st.Name}." : "Check condition changed; dropped and re-added."));
                // Pre-checks run before anything changes, so only when every column the condition uses exists today.
                var ids = SqlText.Identifiers(clause);
                var newColumns = st.Columns.Where(c => tt.Column(c.Name) is null && ids.Contains(c.Name)).Select(c => c.Name).ToList();
                if (tt.RowCount > 0 && newColumns.Count == 0)
                {
                    d.Findings.Add(Finding.Of(Severity.Warning, "CHK002", $"Existing rows that break the condition make ADD CONSTRAINT fail (SQL0544).", "A pre-check counts them; it must return 0."));
                    d.PreChecks.Add(new PreCheck($"Rows satisfy {sc.Name}", $"SELECT COUNT(*) FROM {Ddl.Q(p.TargetLibrary, tt.Name)} WHERE NOT ({clause})"));
                }
                d.DependsOn.AddRange(DependsOnColumns(p.TargetLibrary, st.Name, newColumns));
                Add(d);
            }
            foreach (var tc in tt.Checks.Where(x => !st.Checks.Any(y => Eq(y.Name, x.Name))))
            {
                var d = NewDiff(p, ObjectKind.CheckConstraint, ChangeKind.Removed, tc.Name, tt.Name);
                d.Target = tc; d.TargetDdl = $"CHECK ({tc.Clause})"; d.DefaultSelected = false; d.Destructive = true;
                d.Findings.Add(Finding.Of(Severity.Warning, "CHK003", $"Check constraint exists only in {I.Target.Key}: rows it rejects there are accepted in {I.Source.Key}."));
                Add(d);
            }
        }

        // ------------------------------------------------------------ aliases

        private void CompareAliases(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sa in s.Aliases)
            {
                var ta = t.Aliases.FirstOrDefault(x => Eq(x.Name, sa.Name));
                var baseLib = I.Map.MapLibrary(sa.BaseLibrary);
                if (ta is not null && Eq(ta.BaseLibrary, baseLib) && Eq(ta.BaseTable, sa.BaseTable) && Eq(ta.Member, sa.Member)) continue;
                var d = NewDiff(p, ObjectKind.Alias, ta is null ? ChangeKind.Added : ChangeKind.Modified, sa.Name);
                d.Source = new AliasDef { Name = sa.Name, BaseLibrary = baseLib, BaseTable = sa.BaseTable, Member = sa.Member };
                d.Target = ta;
                d.SourceDdl = AliasDdl(p.TargetLibrary, (AliasDef)d.Source);
                d.TargetDdl = ta is null ? null : AliasDdl(p.TargetLibrary, ta);
                d.Findings.Add(Finding.Of(Severity.Info, "ALS001", ta is null ? "New alias." : "Alias points somewhere else."));
                if (I.Map.ForeignLibraries.TryGetValue(sa.BaseLibrary, out var owner))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "ALS002", $"Alias points at {sa.BaseLibrary}, which belongs to {owner}."));
                else if (!I.Map.IsMapped(sa.BaseLibrary) && !Eq(sa.BaseLibrary, p.SourceLibrary))
                    d.Findings.Add(Finding.Of(Severity.Warning, "ALS003", $"Alias points at {sa.BaseLibrary}, which is not mapped; it is kept as-is in {I.Target.Key}."));
                else if (TargetLib(baseLib)?.Table(sa.BaseTable) is null && !_addedTables.ContainsKey($"{baseLib}.{sa.BaseTable}"))
                    d.Findings.Add(Finding.Of(Severity.Warning, "ALS004", $"Base table {baseLib}.{sa.BaseTable} does not exist in {I.Target.Key}; the alias is created but every query through it fails."));
                if (_addedTables.TryGetValue($"{baseLib}.{sa.BaseTable}", out var tbl)) d.DependsOn.Add(tbl.Id);
                Add(d);
            }
            foreach (var ta in t.Aliases.Where(x => !s.Aliases.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.Alias, ta.Name, ta, AliasDdl(p.TargetLibrary, ta), "ALS005"));
        }

        internal static string AliasDdl(string lib, AliasDef a) =>
            $"CREATE OR REPLACE ALIAS {Ddl.Q(lib, a.Name)} FOR {Ddl.Q(a.BaseLibrary, a.BaseTable)}{(string.IsNullOrEmpty(a.Member) ? "" : $" ({SqlText.Id(a.Member)})")}";

        // ------------------------------------------------------------ data areas

        private void CompareDataAreas(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            // Db2 for i sequences are *DTAARA objects; the catalog lists them as data areas too.
            bool IsSequence(SchemaSnapshot snap, string name) => snap.Sequences.Any(q => Eq(q.Name, name));
            foreach (var sa in s.DataAreas.Where(x => !IsSequence(s, x.Name)))
            {
                var ta = t.DataAreas.FirstOrDefault(x => Eq(x.Name, sa.Name));
                var remap = I.Map.Remap(sa.Value);
                var defDiff = ta is null || !Eq(ta.Type, sa.Type) || ta.Length != sa.Length || ta.Decimals != sa.Decimals || !Eq(ta.Text, sa.Text);
                var valueDiff = ta is not null && !string.Equals(remap.Text?.TrimEnd(), ta.Value?.TrimEnd(), StringComparison.Ordinal);
                if (!defDiff && !valueDiff) continue;

                var d = NewDiff(p, ObjectKind.DataArea, ta is null ? ChangeKind.Added : defDiff ? ChangeKind.Modified : ChangeKind.Drift, sa.Name);
                d.Source = new DataAreaDef { Name = sa.Name, Type = sa.Type, Length = sa.Length, Decimals = sa.Decimals, Text = sa.Text, Value = remap.Text };
                d.Target = ta;
                d.SourceDdl = DataAreaText(sa, remap.Text); d.TargetDdl = ta is null ? null : DataAreaText(ta, ta.Value);
                if (ta is null)
                    d.Findings.Add(Finding.Of(Severity.Info, "DTA005", "New data area; created with the source value (library names mapped). Review the value for this environment."));
                else if (defDiff)
                {
                    d.Findings.Add(Finding.Of(Severity.Warning, "DTA001", $"Definition differs. The data area is re-created and keeps {I.Target.Key}'s current value.", "Programs that have it locked (ALCOBJ) make the step fail."));
                    PreserveAuthorities(t, d, sa.Name, "*DTAARA");
                }
                else
                {
                    d.DefaultSelected = false;
                    d.Findings.Add(Finding.Of(Severity.Info, "DTA002", "Only the value differs. Values are usually environment settings, so this is not promoted unless you tick it."));
                }
                if (remap.Rewrites.Count > 0)
                    d.Findings.Add(Finding.Of(Severity.Warning, "DTA003", $"The value names {string.Join(", ", remap.Rewrites.Select(r => r.From).Distinct())}; it is rewritten to {string.Join(", ", remap.Rewrites.Select(r => r.To).Distinct())}.", autoFixed: true));
                foreach (var f in remap.Foreign.GroupBy(x => x.Library, Ci))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "DTA004", $"The value names {f.Key}, which belongs to {f.First().OwnerEnvironment}."));
                Add(d);
            }
            foreach (var ta in t.DataAreas.Where(x => !IsSequence(t, x.Name) && !s.DataAreas.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.DataArea, ta.Name, ta, DataAreaText(ta, ta.Value), "DTA006"));
        }

        // Shown in the browser: secret-looking values are masked here; the planner reads the real value from the object.
        private static string DataAreaText(DataAreaDef a, string? value) =>
            $"{a.Type} LEN({a.Length}{(a.Type.Equals("*DEC", StringComparison.OrdinalIgnoreCase) ? " " + a.Decimals : "")})\nTEXT '{a.Text}'\nVALUE '{Masking.DataArea(a.Name, value)}'";

        // ------------------------------------------------------------ check-pending constraints

        /// <summary>
        /// A constraint left CHECK PENDING (after CPYF / RSTOBJ / a failed ALTER) restricts access to the table; any
        /// deployment that touches it will fail or be blocked until EDTCPCST / CHGPFCST resolves it.
        /// </summary>
        private void CheckPending(LibraryPair p, SchemaSnapshot t)
        {
            foreach (var fk in t.ForeignKeys.Where(f => f.CheckPending))
            {
                var finding = Finding.Of(Severity.Blocker, "FK011",
                    $"{fk.Name} on {fk.Table} is CHECK PENDING in {I.Target.Key} (parent {fk.RefLibrary}.{fk.RefTable}). The table is restricted until the orphan rows are fixed.",
                    $"Find the rows with the orphan query, fix or remove them, then CHGPFCST FILE({p.TargetLibrary}/...) CST({fk.Name}) STATE(*ENABLED).");
                var id = Difference.MakeId(ObjectKind.ForeignKey, p.Logical, fk.Name);
                if (_diffs.FirstOrDefault(x => x.Id == id) is { } existing) { existing.Findings.Add(finding); continue; }
                var d = NewDiff(p, ObjectKind.ForeignKey, ChangeKind.Drift, fk.Name);
                d.Source = fk; d.Target = fk; d.DefaultSelected = false;
                d.TargetDdl = Ddl.AddForeignKey(p.TargetLibrary, fk, fk.RefLibrary);
                d.Details.Add("CHECK PENDING");
                d.Findings.Add(finding);
                Add(d);
            }
        }

        // ------------------------------------------------------------ data queues

        private void CompareDataQueues(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sq in s.DataQueues)
            {
                var tq = t.DataQueues.FirstOrDefault(x => Eq(x.Name, sq.Name));
                if (tq is not null && tq.MaxLength == sq.MaxLength && Eq(tq.Sequence, sq.Sequence) && tq.KeyLength == sq.KeyLength
                    && tq.IncludeSenderId == sq.IncludeSenderId && Eq(tq.Text, sq.Text)) continue;
                var d = NewDiff(p, ObjectKind.DataQueue, tq is null ? ChangeKind.Added : ChangeKind.Modified, sq.Name);
                d.Source = sq; d.Target = tq;
                d.SourceDdl = Ddl.CreateDataQueue(p.TargetLibrary, sq); d.TargetDdl = tq is null ? null : Ddl.CreateDataQueue(p.TargetLibrary, tq);
                if (tq is null) d.Findings.Add(Finding.Of(Severity.Info, "DTQ002", "New data queue."));
                else
                {
                    d.Destructive = true;
                    d.Findings.Add(Finding.Of(Severity.Warning, "DTQ001", "Definition differs: the queue is deleted and re-created, which loses any entries on it.",
                        "A pre-check confirms the queue is empty at deploy time. Stop the jobs that feed it first."));
                    d.PreChecks.Add(new PreCheck($"{sq.Name} is empty",
                        $"SELECT COALESCE(SUM(CURRENT_MESSAGES), 0) FROM QSYS2.DATA_QUEUE_INFO WHERE DATA_QUEUE_LIBRARY = {SqlText.Literal(p.TargetLibrary)} AND DATA_QUEUE_NAME = {SqlText.Literal(sq.Name)}"));
                    PreserveAuthorities(t, d, sq.Name, "*DTAQ");
                }
                Add(d);
            }
            foreach (var tq in t.DataQueues.Where(x => !s.DataQueues.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.DataQueue, tq.Name, tq, Ddl.CreateDataQueue(p.TargetLibrary, tq), "DTQ003"));
        }

        // ------------------------------------------------------------ job descriptions

        private void CompareJobDescriptions(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sj in s.JobDescriptions)
            {
                var tj = t.JobDescriptions.FirstOrDefault(x => Eq(x.Name, sj.Name));
                var libl = sj.LibraryList.Select(I.Map.MapLibrary).ToList();
                var user = I.Profiles?.Map(sj.User) ?? sj.User;
                var jobq = I.Map.MapQualifiedObject(sj.JobQueue);
                var mapped = new JobDescriptionDef { Name = sj.Name, LibraryList = libl, User = user, JobQueue = jobq, Text = sj.Text };
                if (tj is not null && tj.LibraryList.SequenceEqual(libl, Ci) && Eq(tj.User, user) && Eq(tj.JobQueue, jobq) && Eq(tj.Text, sj.Text)) continue;

                var d = NewDiff(p, ObjectKind.JobDescription, tj is null ? ChangeKind.Added : ChangeKind.Modified, sj.Name);
                d.Source = mapped; d.Target = tj;
                d.SourceDdl = JobdText(mapped); d.TargetDdl = tj is null ? null : JobdText(tj);
                d.Findings.Add(Finding.Of(Severity.Info, "JOBD001", tj is null ? "New job description." : "Job description differs (library list, user, job queue or text)."));
                var foreign = sj.LibraryList.Where(l => I.Map.ForeignLibraries.ContainsKey(l)).ToList();
                if (foreign.Count > 0)
                    d.Findings.Add(Finding.Of(Severity.Blocker, "JOBD002", $"Initial library list contains {string.Join(", ", foreign)}, which belong to another environment. Jobs would read the wrong data."));
                var unmapped = sj.LibraryList.Where(l => !I.Map.IsMapped(l) && !l.StartsWith('Q') && !I.Map.ForeignLibraries.ContainsKey(l)).ToList();
                if (unmapped.Count > 0)
                    d.Findings.Add(Finding.Of(Severity.Warning, "JOBD003", $"Library list entries {string.Join(", ", unmapped)} are not mapped; they are kept as-is in {I.Target.Key}."));
                if (I.Profiles is not null && I.Profiles.IsForeign(sj.User, out var env))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "JOBD004", $"USER({sj.User}) is {env}'s profile."));
                Add(d);
            }
            foreach (var tj in t.JobDescriptions.Where(x => !s.JobDescriptions.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.JobDescription, tj.Name, tj, JobdText(tj), "JOBD005"));
        }

        private static string JobdText(JobDescriptionDef j) =>
            $"INLLIBL({string.Join(" ", j.LibraryList)})\nUSER({j.User})\nJOBQ({j.JobQueue})\nTEXT '{j.Text}'";

        // ------------------------------------------------------------ message descriptions

        private void CompareMessages(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sm in s.Messages)
            {
                var tm = t.Messages.FirstOrDefault(x => Eq(x.File, sm.File) && Eq(x.Id, sm.Id));
                if (tm is not null && tm.Text == sm.Text && (tm.SecondLevel ?? "") == (sm.SecondLevel ?? "") && tm.Severity == sm.Severity) continue;
                var d = NewDiff(p, ObjectKind.Message, tm is null ? ChangeKind.Added : ChangeKind.Modified, sm.Id, sm.File);
                d.Source = sm; d.Target = tm;
                d.SourceDdl = MsgText(sm); d.TargetDdl = tm is null ? null : MsgText(tm);
                d.Findings.Add(Finding.Of(Severity.Info, "MSG001", tm is null ? $"New message {sm.Id} in {sm.File}." : $"Message {sm.Id} text or severity differs."));
                if (tm is null && !t.Messages.Any(x => Eq(x.File, sm.File)))
                    d.Findings.Add(Finding.Of(Severity.Info, "MSG003", $"{p.TargetLibrary} has no messages in {sm.File}; the script creates the message file first (fails harmlessly with CPF2112 if an empty one exists)."));
                Add(d);
            }
            foreach (var tm in t.Messages.Where(x => !s.Messages.Any(y => Eq(y.File, x.File) && Eq(y.Id, x.Id))))
            {
                var d = TargetOnly(p, ObjectKind.Message, tm.Id, tm, MsgText(tm), "MSG002");
                d.Parent = tm.File; d.Id = Difference.MakeId(ObjectKind.Message, p.Logical, tm.Id, tm.File);
                Add(d);
            }
        }

        private static string MsgText(MessageDef m) => $"{m.Id} SEV({m.Severity})\n{m.Text}" + (string.IsNullOrEmpty(m.SecondLevel) ? "" : "\n" + m.SecondLevel);

        // ------------------------------------------------------------ object authorities and owners

        private void CompareAuthorities(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            // A developer's personal library carries the developer's own authorities; those never belong in a master library.
            if (I.Mode == CompareMode.Library || I.SourceOnly || I.Profiles is null) return;
            foreach (var grp in s.Authorities.GroupBy(a => (Obj: a.Object.ToUpperInvariant(), Type: a.ObjectType.ToUpperInvariant())))
            {
                var tgt = t.Authorities.Where(a => Eq(a.Object, grp.Key.Obj) && Eq(a.ObjectType, grp.Key.Type)).ToList();
                var changes = new List<AttributeChange>();
                var grants = new List<ObjectAuthorityDef>();
                var d = NewDiff(p, ObjectKind.Authority, tgt.Count == 0 ? ChangeKind.Added : ChangeKind.Modified, $"{grp.Key.Obj} {grp.Key.Type}");
                foreach (var a in grp)
                {
                    var user = a.User == "*OWNER" ? a.User : I.Profiles.Map(a.User);
                    var value = a.User == "*OWNER" ? I.Profiles.Map(a.Authority) : a.Authority;
                    var current = tgt.FirstOrDefault(x => Eq(x.User, user))?.Authority;
                    if (Eq(current, value)) continue;
                    changes.Add(new AttributeChange(user == "*OWNER" ? "Owner" : $"Authority {user}", current, value));
                    grants.Add(new ObjectAuthorityDef { Object = a.Object, ObjectType = a.ObjectType, User = user, Authority = value });

                    if (I.Profiles.IsForeign(a.User, out var env) || (a.User == "*OWNER" && I.Profiles.IsForeign(a.Authority, out env)))
                        d.Findings.Add(Finding.Of(Severity.Blocker, "AUT003", $"{(a.User == "*OWNER" ? a.Authority : a.User)} is {env}'s profile; granting it here would give another environment's account access."));
                    if (user == "*PUBLIC" && Rank(value) > Rank(current ?? "*EXCLUDE"))
                        d.Findings.Add(Finding.Of(I.Target.Tier == EnvironmentTier.Prod ? Severity.Blocker : Severity.Warning, "AUT001",
                            $"Public authority widens from {current ?? "none"} to {value} in {I.Target.Key}.", "Prefer private authority to a group profile."));
                    if (user == "*OWNER")
                        d.Findings.Add(Finding.Of(Severity.Warning, "AUT004", $"Owner differs ({current ?? "unknown"} → {value}). Objects created by the script are owned by the deploying profile until this runs."));
                    if (!AuthorityRank.Contains(value, Ci) && user != "*OWNER")
                        d.Findings.Add(Finding.Of(Severity.Warning, "AUT006", $"{user} has a user-defined authority set ({value}); it cannot be granted with one command.", "Grant the specific object and data authorities by hand."));
                }
                foreach (var extra in tgt.Where(x => x.User != "*OWNER" && !grp.Any(a => Eq(I.Profiles.Map(a.User), x.User))))
                {
                    changes.Add(new AttributeChange($"Authority {extra.User}", extra.Authority, "(not in source — kept)"));
                    d.Findings.Add(Finding.Of(Severity.Info, "AUT005", $"{extra.User} has {extra.Authority} only in {I.Target.Key}. It is kept (never revoked automatically)."));
                }
                if (changes.Count == 0) continue;
                d.Source = grants; d.Target = tgt;
                d.Changes.AddRange(changes);
                d.Details.Add(string.Join("; ", changes.Take(3).Select(c => $"{c.Attribute}: {c.Before ?? "none"} → {c.After}")));
                if (!d.Findings.Any(f => f.Severity > Severity.Info))
                    d.Findings.Insert(0, Finding.Of(Severity.Info, "AUT002", "Authorities differ; the script grants the source authorities (REPLACE) after the objects exist."));
                Add(d);
            }
        }

        private static int Rank(string authority)
        {
            var i = Array.FindIndex(AuthorityRank, x => x.Equals(authority, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? 1 : i;
        }

        // ------------------------------------------------------------ user profiles / service accounts

        private void CompareProfiles()
        {
            if (I.Profiles is null || I.Mode != CompareMode.Promotion && I.Mode != CompareMode.History) return;
            var pairs = I.Profiles.Pairs.ToList();
            // Same-name tracked profiles (group profiles, shared accounts).
            foreach (var sp in I.SourceProfiles.Where(x => I.Profiles.IsTracked(x.Name) && !pairs.Any(pp => Eq(pp.Source, x.Name))))
                pairs.Add((sp.Name, sp.Name, sp.Name));
            var logical = new LibraryPair("*SYSTEM", "", "");

            foreach (var (name, srcName, tgtName) in pairs)
            {
                var sp = I.SourceProfiles.FirstOrDefault(x => Eq(x.Name, srcName));
                if (sp is null) continue;
                if (tgtName is null)
                {
                    _global.Add(Finding.Of(Severity.Warning, "USR007", $"Profile {name} ({srcName}) has no mapping for {I.Target.Key}. Add it to that environment's Profiles map."));
                    continue;
                }
                var tp = I.TargetProfiles.FirstOrDefault(x => Eq(x.Name, tgtName));
                var want = Translate(sp, tgtName);
                var d = NewDiff(logical with { SourceLibrary = srcName, TargetLibrary = tgtName }, ObjectKind.UserProfile, tp is null ? ChangeKind.Added : ChangeKind.Modified, tgtName);
                d.Logical = "*SYSTEM";
                d.Source = want; d.Target = tp;
                d.SourceDdl = ProfileText(want); d.TargetDdl = tp is null ? null : ProfileText(tp);

                if (tp is not null && ProfileText(want) == ProfileText(tp)) continue;
                d.Findings.Add(Finding.Of(Severity.Info, "USR001", tp is null
                    ? $"{(want.IsServiceAccount ? "Service account" : "Profile")} {tgtName} does not exist in {I.Target.Key}; it is created with PASSWORD(*NONE). Passwords are never copied — the security team sets one if it is needed."
                    : "Profile attributes differ."));

                var added = want.SpecialAuthorities.Except(tp?.SpecialAuthorities ?? [], Ci).Where(a => Powerful.Contains(a, Ci)).ToList();
                if (added.Count > 0)
                    d.Findings.Add(Finding.Of(Severity.Blocker, "USR002", $"Grants {string.Join(", ", added)} to {tgtName} in {I.Target.Key}. Powerful special authorities need the security officer, not a turnover.",
                        "Remove them from the source profile or have security grant them separately."));
                if (Array.IndexOf(ClassRank, want.UserClass.ToUpperInvariant()) > Array.IndexOf(ClassRank, (tp?.UserClass ?? "*USER").ToUpperInvariant()))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "USR003", $"Raises user class to {want.UserClass}."));
                if (tp is not null && Eq(tp.Status, "*DISABLED") && Eq(want.Status, "*ENABLED"))
                    d.Findings.Add(Finding.Of(Severity.Warning, "USR004", $"{tgtName} is disabled in {I.Target.Key}, possibly on purpose. The script does not change STATUS; enable it by hand if intended."));
                if (tp is not null && !Eq(tp.GroupProfile, want.GroupProfile))
                    d.Findings.Add(Finding.Of(Severity.Warning, "USR005", $"Group profile {tp.GroupProfile} → {want.GroupProfile}: changes every object authority inherited through the group."));
                foreach (var (what, lib) in new[] { ("current library", sp.CurrentLibrary), ("job description", sp.JobDescription.Split('/')[0]), ("initial program", sp.InitialProgram.Split('/')[0]) })
                    if (I.Map.ForeignLibraries.TryGetValue(lib, out var env))
                        d.Findings.Add(Finding.Of(Severity.Blocker, "USR006", $"The {what} uses {lib}, which belongs to {env}."));
                if (I.Profiles.IsForeign(sp.GroupProfile, out var genv))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "USR006", $"Group profile {sp.GroupProfile} belongs to {genv}."));
                Add(d);
            }
        }

        private UserProfileDef Translate(UserProfileDef s, string targetName) => new()
        {
            Name = targetName, Status = s.Status, UserClass = s.UserClass, SpecialAuthorities = s.SpecialAuthorities.ToList(),
            GroupProfile = I.Profiles!.Map(s.GroupProfile), SupplementalGroups = s.SupplementalGroups.Select(I.Profiles.Map).ToList(),
            CurrentLibrary = I.Map.MapLibrary(s.CurrentLibrary), InitialProgram = I.Map.MapQualifiedObject(s.InitialProgram),
            InitialMenu = I.Map.MapQualifiedObject(s.InitialMenu), LimitCapabilities = s.LimitCapabilities,
            JobDescription = I.Map.MapQualifiedObject(s.JobDescription), NoPassword = s.NoPassword, Text = s.Text,
        };

        internal static string ProfileText(UserProfileDef p) =>
            $"USRCLS({p.UserClass}) STATUS({p.Status})\nSPCAUT({(p.SpecialAuthorities.Count == 0 ? "*NONE" : string.Join(" ", p.SpecialAuthorities.OrderBy(x => x)))})\n" +
            $"GRPPRF({p.GroupProfile}) SUPGRPPRF({(p.SupplementalGroups.Count == 0 ? "*NONE" : string.Join(" ", p.SupplementalGroups))})\n" +
            $"CURLIB({p.CurrentLibrary}) INLPGM({p.InitialProgram}) INLMNU({p.InitialMenu}) LMTCPB({p.LimitCapabilities})\n" +
            $"JOBD({p.JobDescription}) PASSWORD({(p.NoPassword ? "*NONE" : "set")})\nTEXT '{p.Text}'";

        // ------------------------------------------------------------ helpers

        private Difference TargetOnly(LibraryPair p, ObjectKind kind, string name, object target, string? ddl, string code)
        {
            var d = NewDiff(p, kind, ChangeKind.Removed, name);
            d.Target = target; d.TargetDdl = ddl; d.DefaultSelected = false; d.Destructive = true;
            d.Findings.Add(Finding.Of(Severity.Info, code, $"{name} exists only in {I.Target.Key}. Not deleted unless ticked."));
            return d;
        }

        /// <summary>
        /// Personal / developer library mode: the source holds only the objects being changed, so anything that exists
        /// only in the master library is expected and not reported. Members of objects that ARE in the source (columns,
        /// check constraints, an index on a table the developer copied) are still compared.
        /// </summary>
        private void DropTargetOnlyObjects(List<(LibraryPair Pair, SchemaSnapshot S, SchemaSnapshot T)> pairs)
        {
            // Developer copies rarely carry every index and constraint, so master-only indexes and foreign keys are
            // expected too. Columns and check constraints missing from a copied table are real changes and stay.
            _diffs.RemoveAll(d => d.Change == ChangeKind.Removed && d.Kind is not (ObjectKind.Column or ObjectKind.CheckConstraint));
            _global.Add(Finding.Of(Severity.Info, "LIB005", "Developer-library mode: objects that exist only in the target are not listed."));
        }


    }
}
