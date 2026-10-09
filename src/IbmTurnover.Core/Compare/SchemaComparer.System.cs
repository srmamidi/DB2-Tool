using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

/// <summary>Binding directories (per library) and the partition-wide objects: authorization lists, job schedule
/// entries, system values and IFS stream files.</summary>
public sealed partial class SchemaComparer
{
    private sealed partial class Run
    {
        private static readonly LibraryPair SystemPair = new("*SYSTEM", "", "");

        /// <summary>Security system values: a turnover never changes these (the security officer does).</summary>
        internal static readonly string[] SecuritySystemValues =
        [
            "QSECURITY", "QAUDCTL", "QAUDLVL", "QAUDLVL2", "QALWOBJRST", "QCRTAUT", "QMAXSIGN", "QMAXSGNACN", "QRETSVRSEC",
            "QINACTITV", "QINACTMSGQ", "QLMTSECOFR", "QALWUSRDMN", "QVFYOBJRST", "QFRCCVNRST", "QUSEADPAUT", "QSSLPCL",
        ];

        // ------------------------------------------------------------ binding directories

        private void CompareBindingDirectories(LibraryPair p, SchemaSnapshot s, SchemaSnapshot t)
        {
            foreach (var sb in s.BindingDirectories)
            {
                var tb = t.BindingDirectories.FirstOrDefault(x => Eq(x.Name, sb.Name));
                var want = new BindingDirectoryDef
                {
                    Name = sb.Name, Text = sb.Text,
                    Entries = sb.Entries.Select(e => new BindingEntryDef
                    {
                        Library = e.Library.StartsWith('*') ? e.Library : I.Map.MapLibrary(e.Library), Object = e.Object, Type = e.Type, Activation = e.Activation,
                    }).ToList(),
                };
                if (tb is not null && BindingText(tb) == BindingText(want)) continue;
                var d = NewDiff(p, ObjectKind.BindingDirectory, tb is null ? ChangeKind.Added : ChangeKind.Modified, sb.Name);
                d.Source = want; d.Target = tb;
                d.SourceDdl = BindingText(want); d.TargetDdl = tb is null ? null : BindingText(tb);
                d.Findings.Add(Finding.Of(Severity.Info, "BND001", tb is null
                    ? $"New binding directory ({want.Entries.Count} entries)."
                    : "Entries differ: programs compiled with BNDDIR() would bind different modules or service programs."));
                foreach (var e in sb.Entries.Where(e => I.Map.ForeignLibraries.ContainsKey(e.Library)))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "BND002", $"Entry {e.Library}/{e.Object} belongs to {I.Map.ForeignLibraries[e.Library]}.",
                        "Use *LIBL or a library mapped for this environment."));
                Add(d);
            }
            foreach (var tb in t.BindingDirectories.Where(x => !s.BindingDirectories.Any(y => Eq(y.Name, x.Name))))
                Add(TargetOnly(p, ObjectKind.BindingDirectory, tb.Name, tb, BindingText(tb), "BND003"));
        }

        internal static string BindingText(BindingDirectoryDef b) =>
            $"TEXT '{b.Text}'\n" + string.Join("\n", b.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.ToString()));

        // ------------------------------------------------------------ partition-wide objects

        private void CompareSystemObjects()
        {
            // Library-to-library work stays inside one partition; snapshots taken before these were read have none.
            if (I.Mode == CompareMode.Library || I.SourceSystem is null || I.TargetSystem is null) return;
            CompareAuthorizationLists(I.SourceSystem, I.TargetSystem);
            CompareJobSchedules(I.SourceSystem, I.TargetSystem);
            CompareSystemValues(I.SourceSystem, I.TargetSystem);
            CompareIfs(I.SourceSystem, I.TargetSystem);
        }

        private string MapUser(string user) => user.StartsWith('*') ? user : I.Profiles?.Map(user) ?? user;

        private Difference SystemDiff(ObjectKind kind, ChangeKind change, string name)
        {
            var d = NewDiff(SystemPair, kind, change, name);
            d.Logical = "*SYSTEM";
            return d;
        }

        private void CompareAuthorizationLists(SystemObjects s, SystemObjects t)
        {
            foreach (var sa in s.AuthorizationLists)
            {
                var ta = t.AuthorizationLists.FirstOrDefault(x => Eq(x.Name, sa.Name));
                var want = new AuthorizationListDef
                {
                    Name = sa.Name, Owner = MapUser(sa.Owner),
                    Entries = sa.Entries.Select(e => new AuthorizationListEntry { User = MapUser(e.User), Authority = e.Authority }).ToList(),
                };
                if (ta is not null && AuthListText(ta) == AuthListText(want)) continue;
                var d = SystemDiff(ObjectKind.AuthorizationList, ta is null ? ChangeKind.Added : ChangeKind.Modified, sa.Name);
                d.Source = want; d.Target = ta;
                d.SourceDdl = AuthListText(want); d.TargetDdl = ta is null ? null : AuthListText(ta);
                d.Findings.Add(Finding.Of(Severity.Warning, "AUTL001", ta is null
                    ? "New authorization list. Objects are not secured by it until AUTL(...) is set on them."
                    : "Users or authorities on the list differ: every object it secures changes access at once."));
                if (ta is not null && !Eq(ta.Owner, want.Owner))
                    d.Findings.Add(Finding.Of(Severity.Warning, "AUTL002", $"Owner {ta.Owner} → {want.Owner} is not changed by the script.", "Change it by hand if intended (CHGOBJOWN OBJTYPE(*AUTL))."));
                foreach (var e in sa.Entries.Where(e => I.Profiles is not null && I.Profiles.IsForeign(e.User, out _)))
                {
                    I.Profiles!.IsForeign(e.User, out var env);
                    d.Findings.Add(Finding.Of(Severity.Blocker, "AUTL003", $"{e.User} is a profile of {env}."));
                }
                foreach (var e in want.Entries.Where(e => !e.Authority.StartsWith('*')))
                    d.Findings.Add(Finding.Of(Severity.Warning, "AUTL004", $"{e.User} has user-defined authority ({e.Authority}); it is set by hand."));
                Add(d);
            }
            foreach (var ta in t.AuthorizationLists.Where(x => !s.AuthorizationLists.Any(y => Eq(y.Name, x.Name))))
                Add(Detached(TargetOnly(SystemPair, ObjectKind.AuthorizationList, ta.Name, ta, AuthListText(ta), "AUTL005")));
        }

        internal static string AuthListText(AuthorizationListDef a) =>
            $"OWNER({a.Owner})\n" + string.Join("\n", a.Entries.OrderBy(e => e.User, StringComparer.OrdinalIgnoreCase).Select(e => $"{e.User} {e.Authority}"));

        private void CompareJobSchedules(SystemObjects s, SystemObjects t)
        {
            var dupS = s.JobSchedules.GroupBy(x => x.Name, Ci).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(Ci);
            var dupT = t.JobSchedules.GroupBy(x => x.Name, Ci).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(Ci);
            foreach (var sj in s.JobSchedules.DistinctBy(x => x.Name, Ci))
            {
                var tj = t.JobSchedules.FirstOrDefault(x => Eq(x.Name, sj.Name));
                var remap = I.Map.Remap(sj.Command);
                var want = new JobScheduleDef
                {
                    Name = sj.Name, Status = tj?.Status ?? sj.Status, Frequency = sj.Frequency, ScheduledDate = sj.ScheduledDate, ScheduledDays = sj.ScheduledDays,
                    ScheduledTime = sj.ScheduledTime, RelativeDays = sj.RelativeDays, Command = remap.Text,
                    JobDescription = I.Map.MapQualifiedObject(sj.JobDescription), JobQueue = I.Map.MapQualifiedObject(sj.JobQueue), User = MapUser(sj.User), Text = sj.Text,
                };
                if (tj is not null && ScheduleText(tj) == ScheduleText(want)) continue;
                var d = SystemDiff(ObjectKind.JobSchedule, tj is null ? ChangeKind.Added : ChangeKind.Modified, sj.Name);
                d.Source = want; d.Target = tj;
                d.SourceDdl = ScheduleText(want); d.TargetDdl = tj is null ? null : ScheduleText(tj);
                d.Findings.Add(Finding.Of(Severity.Info, "JSE001", tj is null ? "New job schedule entry." : "Schedule, command or job attributes differ."));
                if (remap.Rewrites.Count > 0)
                    d.Findings.Add(Finding.Of(Severity.Warning, "JSE002", $"The command names {string.Join(", ", remap.Rewrites.Select(r => r.From).Distinct())}; it is rewritten to {string.Join(", ", remap.Rewrites.Select(r => r.To).Distinct())}.", autoFixed: true));
                foreach (var f in remap.Foreign.GroupBy(x => x.Library, Ci))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "JSE003", $"The command names {f.Key}, which belongs to {f.First().OwnerEnvironment}."));
                if (I.Profiles is not null && I.Profiles.IsForeign(sj.User, out var uenv))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "JSE003", $"Runs as {sj.User}, a profile of {uenv}."));
                if (dupS.Contains(sj.Name) || dupT.Contains(sj.Name))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "JSE004", $"There are several entries named {sj.Name}; ENTRYNBR(*ONLY) cannot address one of them.", "Rename the entries or change them by hand."));
                if (tj is not null && !Eq(tj.Status, "SCHEDULED"))
                    d.Findings.Add(Finding.Of(Severity.Info, "JSE005", $"The entry is {tj.Status} in {I.Target.Key}; it stays {tj.Status}."));
                if (DateTime.TryParse(sj.ScheduledDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                    d.Findings.Add(Finding.Of(Severity.Warning, "JSE006", $"Scheduled for a specific date ({sj.ScheduledDate}): the date format depends on the job, so this entry is set by hand."));
                Add(d);
            }
            foreach (var tj in t.JobSchedules.Where(x => !s.JobSchedules.Any(y => Eq(y.Name, x.Name))).DistinctBy(x => x.Name, Ci))
                Add(Detached(TargetOnly(SystemPair, ObjectKind.JobSchedule, tj.Name, tj, ScheduleText(tj), "JSE007")));
        }

        internal static string ScheduleText(JobScheduleDef j) =>
            $"FRQ({j.Frequency}) SCDDATE({j.ScheduledDate}) SCDDAY({j.ScheduledDays}) SCDTIME({j.ScheduledTime}){(string.IsNullOrEmpty(j.RelativeDays) ? "" : $" RELDAYMON({j.RelativeDays})")}\n" +
            $"CMD({j.Command})\nJOBD({j.JobDescription}) JOBQ({j.JobQueue}) USER({j.User})\nTEXT '{j.Text}'";

        private void CompareSystemValues(SystemObjects s, SystemObjects t)
        {
            foreach (var sv in s.SystemValues)
            {
                var tv = t.SystemValues.FirstOrDefault(x => Eq(x.Name, sv.Name));
                if (tv is null || tv.Value == sv.Value) continue;
                var d = SystemDiff(ObjectKind.SystemValue, ChangeKind.Modified, sv.Name);
                d.Source = sv; d.Target = tv; d.DefaultSelected = false;
                d.SourceDdl = sv.Value; d.TargetDdl = tv.Value;
                d.Findings.Add(Finding.Of(Severity.Warning, "SYS001", $"{sv.Name} is {tv.Value} in {I.Target.Key} and {sv.Value} in {I.Source.Key}. System values are partition settings; change one only on purpose."));
                if (SecuritySystemValues.Contains(sv.Name, Ci) || sv.Name.StartsWith("QPWD", StringComparison.OrdinalIgnoreCase))
                    d.Findings.Add(Finding.Of(Severity.Blocker, "SYS002", $"{sv.Name} is a security system value: the security officer changes it, not a turnover."));
                Add(d);
            }
        }

        private void CompareIfs(SystemObjects s, SystemObjects t)
        {
            var logicals = I.Source.IfsPaths.Keys.Where(I.Target.IfsPaths.ContainsKey).ToList();
            var skipped = 0;
            foreach (var logical in logicals)
            {
                var pair = new LibraryPair(logical, I.Source.IfsPaths[logical], I.Target.IfsPaths[logical]);
                // Paths that differ only by case are legal under /QOpenSys; never let them crash the comparison.
                var tFiles = t.IfsFiles.Where(f => Eq(f.Logical, logical)).GroupBy(f => f.RelativePath, Ci).ToDictionary(g => g.Key, g => g.First(), Ci);
                foreach (var dup in s.IfsFiles.Concat(t.IfsFiles).Where(f => Eq(f.Logical, logical)).GroupBy(f => (f.Logical, f.RelativePath)).Select(g => g.First())
                             .GroupBy(f => f.RelativePath, Ci).Where(g => g.Count() > 1 && g.Select(x => x.RelativePath).Distinct(StringComparer.Ordinal).Count() > 1))
                    _global.Add(Finding.Of(Severity.Warning, "IFS006", $"{logical}: {string.Join(" / ", dup.Select(x => x.RelativePath).Distinct(StringComparer.Ordinal))} differ only by case; the tool compares them as one file. Handle them by hand."));
                var sFiles = s.IfsFiles.Where(f => Eq(f.Logical, logical)).ToList();
                var tDirs = tFiles.Keys.Select(Dir).ToHashSet(Ci);
                foreach (var sf in sFiles)
                {
                    tFiles.TryGetValue(sf.RelativePath, out var tf);
                    if (tf is not null)
                    {
                        if (sf.Hash is not null && tf.Hash is not null ? sf.Hash == tf.Hash && sf.Ccsid == tf.Ccsid : sf.Size == tf.Size && sf.Ccsid == tf.Ccsid)
                        {
                            if (sf.Hash is null || tf.Hash is null) skipped++;
                            continue;
                        }
                    }
                    var d = NewDiff(pair, ObjectKind.IfsFile, tf is null ? ChangeKind.Added : ChangeKind.Modified, sf.RelativePath);
                    d.Source = sf; d.Target = tf;
                    d.SourceDdl = IfsText(sf); d.TargetDdl = tf is null ? null : IfsText(tf);
                    d.Findings.Add(Finding.Of(Severity.Info, "IFS001", tf is null ? $"New stream file under {pair.TargetLibrary}." : "Content differs."));
                    if (IsSecretFile(sf.RelativePath))
                    {
                        d.DefaultSelected = false;
                        d.Findings.Add(Finding.Of(Severity.Warning, "IFS003", "Looks like a key, certificate or credential file; these are usually different per environment on purpose."));
                    }
                    var dir = Dir(sf.RelativePath);
                    if (tf is null && dir.Length > 0 && !tDirs.Contains(dir))
                        d.Findings.Add(Finding.Of(Severity.Warning, "IFS004", $"No file in {I.Target.Key} lives in {pair.TargetLibrary}/{dir} yet; the directory may not exist.",
                            $"Create it first: MKDIR DIR('{pair.TargetLibrary}/{dir}')."));
                    Add(d);
                }
                foreach (var tf in tFiles.Values.Where(f => !sFiles.Any(x => Eq(x.RelativePath, f.RelativePath))))
                    Add(TargetOnly(pair, ObjectKind.IfsFile, tf.RelativePath, tf, IfsText(tf), "IFS005"));
            }
            if (skipped > 0)
                _global.Add(Finding.Of(Severity.Info, "IFS002", $"{skipped} IFS file(s) over Turnover:MaxIfsCompareBytes were compared by size only."));
        }

        private static string Dir(string relative) => relative.LastIndexOf('/') is var i and > 0 ? relative[..i] : "";
        internal static string IfsText(IfsFileDef f) => $"{f.Size:N0} bytes, CCSID {f.Ccsid}{(f.Hash is null ? "" : ", sha " + f.Hash)}";

        internal static bool IsSecretFile(string path) =>
            Masking.IsSensitiveName(path) || new[] { ".key", ".pem", ".p12", ".pfx", ".kdb", ".sth", ".jks", ".env" }.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

        /// <summary>System-wide removals are listed for review only: the planner does not delete them.</summary>
        private static Difference Detached(Difference d)
        {
            d.Logical = "*SYSTEM";
            return d;
        }
    }
}
