using System.Data.Common;
using System.Data.Odbc;
using System.Security.Cryptography;
using System.Text;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Catalog;

/// <summary>Creates open connections to an environment. Swap the ODBC default for another IBM i provider here.</summary>
public interface IDb2ConnectionFactory
{
    Task<DbConnection> OpenAsync(TurnoverEnvironment env, CancellationToken ct);
}

/// <summary>IBM i Access ODBC driver. The connection string comes from configuration (web.config, Key Vault, ...).</summary>
public sealed class OdbcConnectionFactory(Func<string, string?> connectionStrings) : IDb2ConnectionFactory
{
    public async Task<DbConnection> OpenAsync(TurnoverEnvironment env, CancellationToken ct)
    {
        var cs = env.ConnectionStringName is null ? null : connectionStrings(env.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(cs)) throw new TurnoverException($"No connection string for environment '{env.Key}'.");
        var con = new OdbcConnection(Normalize(cs, env.Key));
        try { await con.OpenAsync(ct); }
        catch { await con.DisposeAsync(); throw; }   // repeated failures must not leak driver handles
        return con;
    }

    /// <summary>
    /// Every statement uses SQL naming (LIB.TABLE) and DDL must run without commitment control (DDL in a library
    /// that is not journaled fails with SQL7008 under commitment control). Missing keywords are added; an explicit
    /// contradicting value is a configuration error, raised here rather than as a confusing SQL failure later.
    /// </summary>
    public static string Normalize(string connectionString, string env)
    {
        var b = new OdbcConnectionStringBuilder(connectionString);
        string? Get(params string[] keys) => keys.Select(k => b.TryGetValue(k, out var v) ? Convert.ToString(v) : null).FirstOrDefault(v => v is not null);
        var nam = Get("NAM", "Naming");
        if (nam is not null && nam.Trim() != "0")
            throw new TurnoverException($"Connection string for '{env}' sets NAM={nam} (system naming). This tool needs SQL naming: use NAM=0.");
        var cmt = Get("CMT", "CommitMode");
        if (cmt is not null && cmt.Trim() != "0")
            throw new TurnoverException($"Connection string for '{env}' sets CMT={cmt}. Use CMT=0 (no commitment control): DDL in non-journaled libraries fails otherwise (SQL7008).");
        if (nam is null) b["NAM"] = "0";
        if (cmt is null) b["CMT"] = "0";
        // The driver turns a command timeout into a query-governor limit on ESTIMATED time, which can refuse a large
        // catalog read before it starts (SQL0666). Let the command timeout apply as a plain wait instead.
        if (Get("QueryTimeout", "QRYTIMEOUT") is null) b["QueryTimeout"] = "0";
        return b.ConnectionString;
    }
}

/// <summary>
/// Reads Db2 for i catalogs (QSYS2 views and services, IBM i 7.3+). Read-only: the profile needs *USE on the
/// libraries and *OBJOPR on the catalog views. Side effects are limited to QTEMP (private to the job).
/// Every read is recorded in the <see cref="CatalogTrace"/>; any failure fails the library (never a partial snapshot).
/// </summary>
public sealed class Db2iCatalogProvider(IDb2ConnectionFactory connections, TurnoverOptions options) : ICatalogProvider
{
    private int Timeout => options.Catalog.CommandTimeoutSeconds;

    public async Task<SchemaSnapshot?> GetSnapshotAsync(TurnoverEnvironment env, string library, CatalogTrace trace, CancellationToken ct = default)
    {
        var lib = library.ToUpperInvariant();
        if (!TurnoverOptions.IsValidLibraryName(lib)) throw new TurnoverException($"'{library}' is not a valid library name.");
        await using var con = await trace.Run(lib, "Connect", () => connections.OpenAsync(env, ct));
        var exists = await trace.Run(lib, "Library exists", async () =>
            SqlText.ToLong(await ScalarAsync(con, "SELECT COUNT(*) FROM TABLE(QSYS2.OBJECT_STATISTICS('QSYS', '*LIB', ?)) X", ct, lib)) > 0);
        if (!exists) return null;

        var zone = await trace.Run(lib, "System time zone", () => TimeZoneAsync(con, ct));
        var snap = new SchemaSnapshot { EnvironmentKey = env.Key, Library = lib, Logical = env.LogicalFor(lib) ?? lib };
        await trace.Run(lib, "Tables and columns (SYSTABLES, SYSCOLUMNS)", () => ReadTablesAsync(con, snap, ct));
        await trace.Run(lib, "Keys, foreign keys, checks (SYSCST)", () => ReadConstraintsAsync(con, snap, ct));
        await trace.Run(lib, "Indexes (SYSINDEXES)", () => ReadIndexesAsync(con, snap, ct));
        await trace.Run(lib, "Sequences (SYSSEQUENCES)", () => ReadSequencesAsync(con, snap, ct));
        await trace.Run(lib, "Aliases", () => ReadAliasesAsync(con, snap, ct));
        await trace.Run(lib, "SQL routines (SYSROUTINES)", () => ReadRoutinesAsync(con, snap, ct));
        await trace.Run(lib, "Views (SYSVIEWS)", () => ReadViewsAsync(con, snap, ct));
        await trace.Run(lib, "Triggers (SYSTRIGGERS)", () => ReadTriggersAsync(con, snap, ct));
        await trace.Run(lib, "ILE programs (BOUND_MODULE_INFO)", () => ReadProgramsAsync(con, snap, zone, ct));
        await trace.Run(lib, "DDS, commands, menus, panel groups, OPM (OBJECT_STATISTICS)", () => ReadSourceObjectsAsync(con, snap, zone, ct));
        await trace.Run(lib, "Compile options (PROGRAM_INFO)", () => ReadProgramInfoAsync(con, snap, ct));
        await trace.Run(lib, "Program file references (DSPPGMREF)", () => ReadFileReferencesAsync(con, snap, ct));
        if (options.Catalog.HashSourceMembers)
            await trace.Run(lib, "Source member hashes", () => HashSourcesAsync(con, snap, ct));
        await trace.Run(lib, "Data areas (DATA_AREA_INFO)", () => ReadDataAreasAsync(con, snap, ct));
        await trace.Run(lib, "Data queues (DATA_QUEUE_INFO)", () => ReadDataQueuesAsync(con, snap, ct));
        await trace.Run(lib, "Job descriptions (JOB_DESCRIPTION_INFO)", () => ReadJobDescriptionsAsync(con, snap, ct));
        await trace.Run(lib, "Messages (MESSAGE_FILE_DATA)", () => ReadMessagesAsync(con, snap, ct));
        await trace.Run(lib, "Authorities (OBJECT_PRIVILEGES)", () => ReadAuthoritiesAsync(con, snap, ct));
        await trace.Run(lib, "Binding directories (BINDING_DIRECTORY_INFO)", () => ReadBindingDirectoriesAsync(con, snap, ct));
        await trace.Run(lib, "Key maxima (identity / sequence columns)", () => ReadKeyMaximaAsync(con, snap, ct));
        return snap;
    }

    public async Task<IReadOnlyList<UserProfileDef>> GetProfilesAsync(TurnoverEnvironment env, IReadOnlyCollection<string> names, IReadOnlyCollection<string> patterns, CatalogTrace trace, CancellationToken ct = default)
    {
        var exact = names.Where(TurnoverOptions.IsValidLibraryName).ToList();
        var likes = patterns.Where(p => p.EndsWith('*')).Select(p => p[..^1] + "%").ToList();
        exact.AddRange(patterns.Where(p => !p.EndsWith('*') && TurnoverOptions.IsValidLibraryName(p)));
        if (exact.Count == 0 && likes.Count == 0) return [];
        await using var con = await trace.Run("*SYSTEM", "Connect", () => connections.OpenAsync(env, ct));
        return await trace.Run("*SYSTEM", "User profiles (USER_INFO)", async () =>
        {
            var where = string.Join(" OR ", exact.Select(_ => "AUTHORIZATION_NAME = ?").Concat(likes.Select(_ => "AUTHORIZATION_NAME LIKE ?")));
            var sql = $"""
                SELECT AUTHORIZATION_NAME, STATUS, USER_CLASS_NAME, COALESCE(SPECIAL_AUTHORITIES, ''), COALESCE(GROUP_PROFILE_NAME, '*NONE'),
                       COALESCE(SUPPLEMENTAL_GROUP_LIST, ''), COALESCE(CURRENT_LIBRARY_NAME, '*CRTDFT'), INITIAL_PROGRAM_LIBRARY_NAME,
                       COALESCE(INITIAL_PROGRAM_NAME, '*NONE'), INITIAL_MENU_LIBRARY_NAME, COALESCE(INITIAL_MENU_NAME, 'MAIN'),
                       COALESCE(LIMIT_CAPABILITIES, '*NO'), JOB_DESCRIPTION_LIBRARY_NAME, COALESCE(JOB_DESCRIPTION_NAME, 'QDFTJOBD'),
                       COALESCE(NO_PASSWORD_INDICATOR, 'NO'), COALESCE(TEXT_DESCRIPTION, '')
                  FROM QSYS2.USER_INFO WHERE {where}
                """;
            var list = new List<UserProfileDef>();
            await foreach (var r in QueryAsync(con, sql, ct, exact.Concat(likes).Cast<object>().ToArray()))
                list.Add(new UserProfileDef
                {
                    Name = Str(r, 0), Status = Str(r, 1), UserClass = Str(r, 2), SpecialAuthorities = Words(Str(r, 3)), GroupProfile = Str(r, 4),
                    SupplementalGroups = FixedWidth(Str(r, 5), 10), CurrentLibrary = Str(r, 6), InitialProgram = Qualify(Str(r, 7), Str(r, 8)),
                    InitialMenu = Qualify(Str(r, 9), Str(r, 10)), LimitCapabilities = Str(r, 11), JobDescription = Qualify(Str(r, 12), Str(r, 13)),
                    NoPassword = Str(r, 14) == "YES", Text = Str(r, 15),
                });
            return (IReadOnlyList<UserProfileDef>)list;
        }, l => l.Count);
    }

    public async Task<SystemObjects> GetSystemObjectsAsync(TurnoverEnvironment env, TurnoverOptions o, CatalogTrace trace, CancellationToken ct = default)
    {
        var result = new SystemObjects();
        await using var con = await trace.Run("*SYSTEM", "Connect (system objects)", () => connections.OpenAsync(env, ct));
        var zone = await trace.Run("*SYSTEM", "System time zone", () => TimeZoneAsync(con, ct));

        (string Where, object[] Args) Filter(string column, IReadOnlyCollection<string> names)
        {
            var exact = names.Where(n => !n.EndsWith('*')).ToList();
            var likes = names.Where(n => n.EndsWith('*')).Select(n => n[..^1] + "%").ToList();
            var where = string.Join(" OR ", exact.Select(_ => $"{column} = ?").Concat(likes.Select(_ => $"{column} LIKE ?")));
            return (where, exact.Concat(likes).Cast<object>().ToArray());
        }

        if (o.TrackedAuthorizationLists.Count > 0)
            await trace.Run("*SYSTEM", "Authorization lists (AUTHORIZATION_LIST_USER_INFO)", async () =>
            {
                var (where, args) = Filter("AUTHORIZATION_LIST", o.TrackedAuthorizationLists);
                var lists = new Dictionary<string, AuthorizationListDef>(StringComparer.OrdinalIgnoreCase);
                await foreach (var r in QueryAsync(con, $"SELECT AUTHORIZATION_LIST, AUTHORIZATION_NAME, OBJECT_AUTHORITY, OWNER FROM QSYS2.AUTHORIZATION_LIST_USER_INFO WHERE {where} ORDER BY AUTHORIZATION_LIST, AUTHORIZATION_NAME", ct, args))
                {
                    var name = Str(r, 0);
                    if (!lists.TryGetValue(name, out var l)) lists[name] = l = new AuthorizationListDef { Name = name, Owner = Str(r, 3) };
                    // The owner's own *ALL entry comes with ownership and is not a grant.
                    if (!Str(r, 1).Equals(l.Owner, StringComparison.OrdinalIgnoreCase))
                        l.Entries.Add(new AuthorizationListEntry { User = Str(r, 1), Authority = Str(r, 2) });
                }
                result.AuthorizationLists.AddRange(lists.Values);
                return lists.Count;
            });

        if (o.TrackedJobSchedules.Count > 0)
            await trace.Run("*SYSTEM", "Job schedule entries (SCHEDULED_JOB_INFO)", async () =>
            {
                var (where, args) = Filter("SCHEDULED_JOB_NAME", o.TrackedJobSchedules);
                var sql = $"""
                    SELECT SCHEDULED_JOB_NAME, SCHEDULED_JOB_ENTRY_NUMBER, STATUS, FREQUENCY, SCHEDULED_DATE_VALUE, SCHEDULED_DATE,
                           COALESCE(SCHEDULED_DAYS, ''), CAST(SCHEDULED_TIME AS CHAR(8)), COALESCE(RELATIVE_DAYS_OF_MONTH, ''), COMMAND_STRING,
                           JOB_DESCRIPTION_LIBRARY_NAME, JOB_DESCRIPTION_NAME, JOB_QUEUE_LIBRARY_NAME, JOB_QUEUE_NAME,
                           USER_PROFILE_FOR_SUBMITTED_JOB, COALESCE(DESCRIPTION, '')
                      FROM QSYS2.SCHEDULED_JOB_INFO WHERE {where}
                    """;
                await foreach (var r in QueryAsync(con, sql, ct, args))
                {
                    // SCHEDULED_DATE_VALUE says which form applies: SCHEDULED_DATE (a DATE column), SCHEDULED_DAYS, *MONTHSTR or *MONTHEND.
                    var kind = Str(r, 4).ToUpperInvariant();
                    var date = kind switch
                    {
                        "SCHEDULED_DATE" => r[5] is DateTime dt ? dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : r[5] is DateOnly d0 ? d0.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "*NONE",
                        "SCHEDULED_DAYS" or "" => "*NONE",
                        _ => kind,
                    };
                    result.JobSchedules.Add(new JobScheduleDef
                    {
                        Name = Str(r, 0), EntryNumber = SqlText.ToInt(r[1]), Status = Str(r, 2), Frequency = Str(r, 3), ScheduledDate = date,
                        ScheduledDays = Str(r, 6) is { Length: > 0 } days ? days.Replace(',', ' ') : "*NONE",
                        ScheduledTime = Str(r, 7).Replace('.', ':'), RelativeDays = Str(r, 8).Replace(',', ' '), Command = Str(r, 9),
                        JobDescription = Str(r, 11) is { Length: > 0 } jd ? Qualify(Str(r, 10), jd) : "*USRPRF",
                        JobQueue = Str(r, 13) is { Length: > 0 } jq ? Qualify(Str(r, 12), jq) : "*JOBD",
                        User = Str(r, 14) is { Length: > 0 } u ? u : "*CURRENT", Text = Str(r, 15),
                    });
                }
                return result.JobSchedules.Count;
            });

        if (o.TrackedSystemValues.Count > 0)
            await trace.Run("*SYSTEM", "System values (SYSTEM_VALUE_INFO)", async () =>
            {
                var (where, args) = Filter("SYSTEM_VALUE_NAME", o.TrackedSystemValues);
                await foreach (var r in QueryAsync(con, $"SELECT SYSTEM_VALUE_NAME, CAST(CURRENT_NUMERIC_VALUE AS VARCHAR(40)), CURRENT_CHARACTER_VALUE FROM QSYS2.SYSTEM_VALUE_INFO WHERE {where}", ct, args))
                    result.SystemValues.Add(r[1] is DBNull
                        ? new SystemValueDef { Name = Str(r, 0), Value = Str(r, 2) }
                        : new SystemValueDef { Name = Str(r, 0), Value = Str(r, 1), Numeric = true });
                return result.SystemValues.Count;
            });

        foreach (var (logical, root) in env.IfsPaths)
            await trace.Run("*SYSTEM", $"IFS {root} (IFS_OBJECT_STATISTICS)", async () =>
            {
                // IGNORE_ERRORS => 'NO': an unreadable sub-directory must fail the read, not look like missing files.
                const string sql = """
                    SELECT PATH_NAME, DATA_SIZE, DATA_CHANGE_TIMESTAMP, CCSID
                      FROM TABLE(QSYS2.IFS_OBJECT_STATISTICS(START_PATH_NAME => ?, SUBTREE_DIRECTORIES => 'YES', OBJECT_TYPE_LIST => '*STMF', IGNORE_ERRORS => 'NO')) X
                    """;
                var prefix = root.TrimEnd('/') + "/";
                var files = new List<IfsFileDef>();
                await foreach (var r in QueryAsync(con, sql, ct, root))
                {
                    var path = Str(r, 0);
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add(new IfsFileDef
                    {
                        Logical = logical, Path = path, RelativePath = path[prefix.Length..], Size = SqlText.ToLong(r[1]) ?? 0,
                        Changed = Utc(r[2], zone), Ccsid = SqlText.ToInt(r[3]),
                    });
                }
                foreach (var f in files.Where(f => f.Size <= o.MaxIfsCompareBytes))
                    f.Hash = await HashIfsAsync(con, f.Path, ct);
                result.IfsFiles.AddRange(files);
                return files.Count;
            });
        return result;
    }

    /// <summary>SHA-256 of a stream file's bytes, read with IFS_READ_BINARY in fixed-size chunks (no end-of-line handling).</summary>
    private async Task<string> HashIfsAsync(DbConnection con, string path, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        await foreach (var r in QueryAsync(con, "SELECT LINE FROM TABLE(QSYS2.IFS_READ_BINARY(PATH_NAME => ?, MAXIMUM_LINE_LENGTH => 32000, END_OF_LINE => 'NONE')) X ORDER BY LINE_NUMBER", ct, path))
            if (r[0] is byte[] bytes) sha.AppendData(bytes);
        return Convert.ToHexString(sha.GetHashAndReset())[..16];
    }

    public async Task<IReadOnlyList<string>?> GetSourceAsync(TurnoverEnvironment env, string library, string file, string member, CancellationToken ct = default)
    {
        foreach (var n in new[] { library, file, member })
            if (!TurnoverOptions.IsValidLibraryName(n)) throw new TurnoverException($"'{n}' is not a valid IBM i name.");
        await using var con = await connections.OpenAsync(env, ct);
        var exists = SqlText.ToLong(await ScalarAsync(con,
            "SELECT COUNT(*) FROM QSYS2.SYSPARTITIONSTAT WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? AND TABLE_PARTITION = ?", ct,
            library.ToUpperInvariant(), file.ToUpperInvariant(), member.ToUpperInvariant())) > 0;
        if (!exists) return null;
        await ExecAsync(con, $"CREATE OR REPLACE ALIAS QTEMP.TOVSRCTXT FOR {library}.{file} ({member})".ToUpperInvariant(), ct);
        var lines = new List<string>();
        await foreach (var r in QueryAsync(con, "SELECT SRCDTA FROM QTEMP.TOVSRCTXT ORDER BY SRCSEQ", ct))
            lines.Add(r[0] is byte[] raw ? Ebcdic.GetString(raw).TrimEnd('\0', ' ') : (Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture) ?? "").TrimEnd());
        return lines;
    }

    public async Task<IReadOnlyList<(string Check, bool Ok, string Message)>> TestAsync(TurnoverEnvironment env, CancellationToken ct = default)
    {
        var results = new List<(string, bool, string)>();
        DbConnection con;
        try
        {
            con = await connections.OpenAsync(env, ct);
            results.Add(("Connect", true, $"server {con.ServerVersion}"));
        }
        catch (Exception ex) when (ex is DbException or TurnoverException or InvalidOperationException)
        {
            results.Add(("Connect", false, ex.Message));
            return results;
        }
        await using (con)
        {
            var lib = env.Libraries.Values.First();
            string[] probes =
            [
                "SELECT COUNT(*) FROM QSYS2.SYSTABLES WHERE TABLE_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSCOLUMNS WHERE TABLE_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSCST WHERE CONSTRAINT_SCHEMA = ? AND CHECK_PENDING = 'YES'",
                "SELECT COUNT(*) FROM QSYS2.SYSCHKCST WHERE CONSTRAINT_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSINDEXES WHERE INDEX_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSSEQUENCES WHERE SEQUENCE_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSROUTINES WHERE ROUTINE_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSVIEWS WHERE TABLE_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSTRIGGERS WHERE TRIGGER_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.BOUND_MODULE_INFO WHERE PROGRAM_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.DATA_AREA_INFO WHERE DATA_AREA_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.DATA_QUEUE_INFO WHERE DATA_QUEUE_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.JOB_DESCRIPTION_INFO WHERE JOB_DESCRIPTION_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.MESSAGE_FILE_DATA WHERE MESSAGE_FILE_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.OBJECT_PRIVILEGES WHERE SYSTEM_OBJECT_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.COMMAND_INFO WHERE COMMAND_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.PROGRAM_INFO WHERE PROGRAM_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.SYSPARTITIONSTAT WHERE TABLE_SCHEMA = ?",
                "SELECT COUNT(*) FROM QSYS2.BINDING_DIRECTORY_INFO WHERE BINDING_DIRECTORY_LIBRARY = ?",
                "SELECT COUNT(*) FROM QSYS2.OBJECT_LOCK_INFO WHERE SYSTEM_OBJECT_SCHEMA = ?",
            ];
            foreach (var sql in probes)
            {
                var name = sql.Split("FROM ")[1].Split(' ')[0];
                try { results.Add((name, true, $"{SqlText.ToLong(await ScalarAsync(con, sql, ct, lib))} row(s) in {lib}")); }
                catch (DbException ex) { results.Add((name, false, ex.Message)); }
            }
            try { results.Add(("QSYS2.USER_INFO", true, $"{SqlText.ToLong(await ScalarAsync(con, "SELECT COUNT(*) FROM QSYS2.USER_INFO WHERE AUTHORIZATION_NAME = CURRENT USER", ct))} row(s) for the connecting profile")); }
            catch (DbException ex) { results.Add(("QSYS2.USER_INFO", false, ex.Message)); }
            string[] system =
            [
                "SELECT COUNT(*) FROM QSYS2.SYSTEM_VALUE_INFO WHERE SYSTEM_VALUE_NAME = 'QDATFMT'",
                "SELECT COUNT(*) FROM QSYS2.AUTHORIZATION_LIST_USER_INFO WHERE AUTHORIZATION_LIST = '*NONE'",
                "SELECT COUNT(*) FROM QSYS2.SCHEDULED_JOB_INFO WHERE SCHEDULED_JOB_NAME = '*NONE'",
            ];
            foreach (var sql in system)
            {
                var name = sql.Split("FROM ")[1].Split(' ')[0];
                try { results.Add((name, true, $"readable ({SqlText.ToLong(await ScalarAsync(con, sql, ct))} row(s) for the probe)")); }
                catch (DbException ex) { results.Add((name, false, ex.Message)); }
            }
            foreach (var root in env.IfsPaths.Values)
                try { results.Add(($"IFS {root}", true, $"{SqlText.ToLong(await ScalarAsync(con, "SELECT COUNT(*) FROM TABLE(QSYS2.IFS_OBJECT_STATISTICS(START_PATH_NAME => ?, SUBTREE_DIRECTORIES => 'NO', IGNORE_ERRORS => 'NO')) X", ct, root))} object(s)")); }
                catch (DbException ex) { results.Add(($"IFS {root}", false, ex.Message)); }
            try { results.Add(("Time zone", true, $"UTC offset {await TimeZoneAsync(con, ct)}")); }
            catch (DbException ex) { results.Add(("Time zone", false, ex.Message)); }
        }
        return results;
    }

    // ------------------------------------------------------------------ readers

    /// <summary>
    /// CURRENT TIMEZONE is DECIMAL(6,0) hhmmss (for example -50000 = UTC-5). Catalog timestamps are local to the
    /// partition, so they are converted to UTC before Dev and Prod timestamps are compared.
    /// </summary>
    private async Task<TimeSpan> TimeZoneAsync(DbConnection con, CancellationToken ct)
    {
        var raw = SqlText.ToLong(await ScalarAsync(con, "SELECT CURRENT TIMEZONE FROM SYSIBM.SYSDUMMY1", ct)) ?? 0;
        var abs = Math.Abs(raw);
        var span = new TimeSpan((int)(abs / 10000), (int)(abs / 100 % 100), (int)(abs % 100));
        return raw < 0 ? -span : span;
    }

    /// <summary>
    /// SYSCOLUMNS.DATA_TYPE uses short codes (TIMESTMP, VARG, VARBIN) and reports REAL/DOUBLE as FLOAT with a byte
    /// length. Generated DDL needs the real type names, so they are translated here, once.
    /// </summary>
    internal static (string Type, int? Length, int? Scale) SqlType(string code, int length, int? scale) => code.Trim().ToUpperInvariant() switch
    {
        "TIMESTMP" or "TIMESTAMP" => ("TIMESTAMP", scale is null or 6 ? null : scale, null),   // fractional digits live in NUMERIC_SCALE
        "VARG" => ("VARGRAPHIC", length, null),
        "VARBIN" => ("VARBINARY", length, null),
        "FLOAT" => (length == 4 ? "REAL" : "DOUBLE", null, null),
        "DECIMAL" or "NUMERIC" => (code.Trim().ToUpperInvariant(), length, scale ?? 0),
        "CHAR" or "VARCHAR" or "GRAPHIC" or "VARGRAPHIC" or "BINARY" or "VARBINARY" or "CLOB" or "BLOB" or "DBCLOB" or "DECFLOAT"
            => (code.Trim().ToUpperInvariant(), length, null),
        var other => (other, null, null),   // INTEGER, BIGINT, SMALLINT, DATE, TIME, XML, ROWID, ...
    };

    private static DateTimeOffset Utc(object value, TimeSpan zone) =>
        value is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), zone).ToUniversalTime() : default;

    private async Task<int> ReadTablesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT T.TABLE_TYPE, T.TABLE_NAME, T.SYSTEM_TABLE_NAME, COALESCE(T.TABLE_TEXT, ''), COALESCE(S.NUMBER_ROWS, 0),
                   COALESCE(O.JOURNALED, 'NO'), O.JOURNAL_NAME
              FROM QSYS2.SYSTABLES T
              LEFT JOIN QSYS2.SYSTABLESTAT S ON S.TABLE_SCHEMA = T.TABLE_SCHEMA AND S.TABLE_NAME = T.TABLE_NAME
              LEFT JOIN TABLE(QSYS2.OBJECT_STATISTICS(?, '*FILE')) O ON O.OBJNAME = T.SYSTEM_TABLE_NAME
             WHERE T.TABLE_SCHEMA = ? AND (T.TABLE_TYPE = 'T' OR (T.TABLE_TYPE = 'P' AND T.FILE_TYPE = 'D'))
            """;
        // TABLE_TYPE 'P' = DDS-described physical file; FILE_TYPE 'D' leaves out source files (QRPGLESRC & co.).
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library, snap.Library))
            snap.Tables.Add(new TableDef
            {
                IsDds = Str(r, 0) == "P",
                Name = Str(r, 1), SystemName = Str(r, 2), Text = Str(r, 3), RowCount = SqlText.ToLong(r[4]) ?? 0,
                Journaled = Str(r, 5) == "YES", JournalName = r[6] as string,
            });

        const string cols = """
            SELECT TABLE_NAME, COLUMN_NAME, SYSTEM_COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, LENGTH, NUMERIC_SCALE,
                   IS_NULLABLE, COLUMN_DEFAULT, CCSID, IS_IDENTITY, COLUMN_TEXT, COLUMN_HEADING
              FROM QSYS2.SYSCOLUMNS WHERE TABLE_SCHEMA = ? ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;
        var byName = snap.Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var n = 0;
        await foreach (var r in QueryAsync(con, cols, ct, snap.Library))
        {
            if (!byName.TryGetValue(Str(r, 0), out var t)) continue; // view column
            var (type, length, scale) = SqlType(Str(r, 4), SqlText.ToInt(r[5]), r[6] is DBNull ? null : SqlText.ToInt(r[6]));
            var ccsid = SqlText.ToInt(r[9]);
            t.Columns.Add(new ColumnDef
            {
                Name = Str(r, 1), SystemName = Str(r, 2), Ordinal = SqlText.ToInt(r[3]), DataType = type,
                Length = length,
                Scale = scale,
                Nullable = Str(r, 7) == "Y", Default = r[8] is DBNull ? null : Str(r, 8),
                Ccsid = ccsid == 0 ? null : ccsid, IsIdentity = Str(r, 10) == "YES",
                Text = r[11] is DBNull ? null : Str(r, 11), Heading = r[12] is DBNull ? null : Str(r, 12),
            });
            n++;
        }
        foreach (var t in snap.Tables) t.FormatLevelId = t.ComputeFormatLevel();
        return n;
    }

    private async Task<int> ReadConstraintsAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string keys = """
            SELECT C.CONSTRAINT_NAME, C.CONSTRAINT_TYPE, C.TABLE_NAME, K.COLUMN_NAME, COALESCE(C.CHECK_PENDING, 'NO')
              FROM QSYS2.SYSCST C JOIN QSYS2.SYSKEYCST K
                ON K.CONSTRAINT_SCHEMA = C.CONSTRAINT_SCHEMA AND K.CONSTRAINT_NAME = C.CONSTRAINT_NAME
             WHERE C.CONSTRAINT_SCHEMA = ? AND C.CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY')
             ORDER BY C.CONSTRAINT_NAME, K.ORDINAL_POSITION
            """;
        var groups = new Dictionary<string, (string Type, string Table, List<string> Cols, bool Pending)>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in QueryAsync(con, keys, ct, snap.Library))
        {
            if (!groups.TryGetValue(Str(r, 0), out var g)) groups[Str(r, 0)] = g = (Str(r, 1), Str(r, 2), new(), Str(r, 4) == "YES");
            g.Cols.Add(Str(r, 3));
        }
        foreach (var (name, g) in groups)
        {
            var t = snap.Table(g.Table);
            if (t is null) continue;
            if (g.Type == "PRIMARY KEY") { t.PrimaryKey = g.Cols; t.PrimaryKeyName = name; }
            else if (g.Type == "UNIQUE") t.UniqueKeys.Add(g.Cols);
        }

        const string refs = """
            SELECT R.CONSTRAINT_NAME, R.UNIQUE_CONSTRAINT_SCHEMA, P.TABLE_NAME, R.DELETE_RULE, R.UPDATE_RULE, PK.COLUMN_NAME
              FROM QSYS2.SYSREFCST R
              JOIN QSYS2.SYSCST P ON P.CONSTRAINT_SCHEMA = R.UNIQUE_CONSTRAINT_SCHEMA AND P.CONSTRAINT_NAME = R.UNIQUE_CONSTRAINT_NAME
              JOIN QSYS2.SYSKEYCST PK ON PK.CONSTRAINT_SCHEMA = P.CONSTRAINT_SCHEMA AND PK.CONSTRAINT_NAME = P.CONSTRAINT_NAME
             WHERE R.CONSTRAINT_SCHEMA = ?
             ORDER BY R.CONSTRAINT_NAME, PK.ORDINAL_POSITION
            """;
        var fks = new Dictionary<string, ForeignKeyDef>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in QueryAsync(con, refs, ct, snap.Library))
        {
            var name = Str(r, 0);
            if (!fks.TryGetValue(name, out var fk))
            {
                if (!groups.TryGetValue(name, out var child)) continue;
                fks[name] = fk = new ForeignKeyDef
                {
                    Name = name, Table = child.Table, Columns = child.Cols, RefLibrary = Str(r, 1), RefTable = Str(r, 2),
                    DeleteRule = Ddl.Rule(Str(r, 3)), UpdateRule = Ddl.Rule(Str(r, 4)), CheckPending = child.Pending,
                };
            }
            fk.RefColumns.Add(Str(r, 5));
        }
        snap.ForeignKeys.AddRange(fks.Values);

        const string checks = """
            SELECT C.CONSTRAINT_NAME, C.TABLE_NAME, K.CHECK_CLAUSE
              FROM QSYS2.SYSCST C JOIN QSYS2.SYSCHKCST K ON K.CONSTRAINT_SCHEMA = C.CONSTRAINT_SCHEMA AND K.CONSTRAINT_NAME = C.CONSTRAINT_NAME
             WHERE C.CONSTRAINT_SCHEMA = ? AND C.CONSTRAINT_TYPE = 'CHECK'
             ORDER BY C.CONSTRAINT_NAME
            """;
        await foreach (var r in QueryAsync(con, checks, ct, snap.Library))
            snap.Table(Str(r, 1))?.Checks.Add(new CheckConstraintDef(Str(r, 0), Str(r, 2)));
        return groups.Count;
    }

    private async Task<int> ReadIndexesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT I.INDEX_NAME, I.SYSTEM_INDEX_NAME, I.TABLE_NAME, I.IS_UNIQUE, K.COLUMN_NAME, K.ORDERING
              FROM QSYS2.SYSINDEXES I JOIN QSYS2.SYSKEYS K ON K.INDEX_SCHEMA = I.INDEX_SCHEMA AND K.INDEX_NAME = I.INDEX_NAME
             WHERE I.INDEX_SCHEMA = ? ORDER BY I.INDEX_NAME, K.ORDINAL_POSITION
            """;
        var map = new Dictionary<string, IndexDef>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
        {
            if (!map.TryGetValue(Str(r, 0), out var ix))
                map[Str(r, 0)] = ix = new IndexDef { Name = Str(r, 0), SystemName = Str(r, 1), Table = Str(r, 2), IsUnique = Str(r, 3) is "U" or "V", UniqueWhereNotNull = Str(r, 3) == "V" };
            ix.Columns.Add(new IndexColumn(Str(r, 4), Str(r, 5) == "D"));
        }
        snap.Indexes.AddRange(map.Values);
        return map.Count;
    }

    private async Task<int> ReadSequencesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT SEQUENCE_NAME, DATA_TYPE, CAST(START AS VARCHAR(64)), CAST(INCREMENT AS VARCHAR(64)),
                   CAST(MINIMUM_VALUE AS VARCHAR(64)), CAST(MAXIMUM_VALUE AS VARCHAR(64)), CYCLE_OPTION, CACHE, NUMERIC_PRECISION
              FROM QSYS2.SYSSEQUENCES WHERE SEQUENCE_SCHEMA = ?
            """;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
        {
            var seq = new SequenceDef
            {
                // DECIMAL(31) sequences can exceed long: ToLong returns null rather than aborting the read.
                // Values arrive as text: a DECIMAL(63) catalog value would overflow .NET decimal inside the driver.
                // CREATE SEQUENCE AS DECIMAL without precision means DECIMAL(5,0), so the precision is carried.
                Name = Str(r, 0), DataType = Str(r, 1) is "DECIMAL" or "NUMERIC" ? $"{Str(r, 1)}({SqlText.ToInt(r[8], 31)},0)" : Str(r, 1),
                Start = SqlText.ToLong(r[2]) ?? 1, Increment = SqlText.ToLong(r[3]) ?? 1,
                MinValue = SqlText.ToLong(r[4]), MaxValue = SqlText.ToLong(r[5]), Cycle = Str(r, 6) == "YES", Cache = SqlText.ToInt(r[7]),
            };
            var b = options.SequenceBindings.FirstOrDefault(x => x.Sequence.Equals(seq.Name, StringComparison.OrdinalIgnoreCase) && x.Logical.Equals(snap.Logical, StringComparison.OrdinalIgnoreCase));
            if (b is not null) { seq.BoundTable = b.Table; seq.BoundColumn = b.Column; }
            // NextValue is deliberately not read: there is no non-consuming catalog column for it (see SEQ009).
            snap.Sequences.Add(seq);
        }
        return snap.Sequences.Count;
    }

    private async Task<int> ReadAliasesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = "SELECT TABLE_NAME, BASE_TABLE_SCHEMA, BASE_TABLE_NAME, BASE_TABLE_MEMBER FROM QSYS2.SYSTABLES WHERE TABLE_SCHEMA = ? AND TABLE_TYPE = 'A'";
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            snap.Aliases.Add(new AliasDef { Name = Str(r, 0), BaseLibrary = Str(r, 1), BaseTable = Str(r, 2), Member = r[3] is DBNull ? null : Str(r, 3) });
        return snap.Aliases.Count;
    }

    private async Task<int> ReadRoutinesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT ROUTINE_NAME, SPECIFIC_NAME, ROUTINE_TYPE, COALESCE(EXTERNAL_LANGUAGE, 'SQL'), EXTERNAL_NAME
              FROM QSYS2.SYSROUTINES WHERE ROUTINE_SCHEMA = ?
            """;
        var list = new List<RoutineDef>();
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            list.Add(new RoutineDef { Name = Str(r, 0), SpecificName = Str(r, 1), Kind = Str(r, 2), Language = Str(r, 3), ExternalName = r[4] as string });

        const string parms = """
            SELECT PARAMETER_MODE, PARAMETER_NAME, DATA_TYPE FROM QSYS2.SYSPARMS
             WHERE SPECIFIC_SCHEMA = ? AND SPECIFIC_NAME = ? ORDER BY ORDINAL_POSITION
            """;
        foreach (var rt in list)
        {
            await foreach (var p in QueryAsync(con, parms, ct, snap.Library, rt.SpecificName))
                rt.Parameters.Add($"{Str(p, 0)} {Str(p, 1)} {Str(p, 2)}".Trim());
            rt.Definition = await GenerateSqlAsync(con, rt.SpecificName, snap.Library, rt.Kind, ct);
        }
        snap.Routines.AddRange(list);
        return list.Count;
    }

    private async Task<int> ReadViewsAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = "SELECT TABLE_NAME, SYSTEM_VIEW_NAME FROM QSYS2.SYSVIEWS WHERE TABLE_SCHEMA = ?";
        var views = new List<ViewDef>();
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            views.Add(new ViewDef { Name = Str(r, 0), SystemName = Str(r, 1) });
        const string deps = "SELECT OBJECT_SCHEMA, OBJECT_NAME FROM QSYS2.SYSVIEWDEP WHERE VIEW_SCHEMA = ? AND VIEW_NAME = ? AND OBJECT_TYPE IN ('TABLE', 'VIEW') ORDER BY OBJECT_SCHEMA, OBJECT_NAME";
        foreach (var v in views)
        {
            await foreach (var r in QueryAsync(con, deps, ct, snap.Library, v.Name)) v.References.Add($"{Str(r, 0)}.{Str(r, 1)}");
            v.Definition = await GenerateSqlAsync(con, v.Name, snap.Library, "VIEW", ct);
        }
        snap.Views.AddRange(views);
        return views.Count;
    }

    private async Task<int> ReadTriggersAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = "SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE FROM QSYS2.SYSTRIGGERS WHERE TRIGGER_SCHEMA = ?";
        var list = new List<TriggerDef>();
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library)) list.Add(new TriggerDef { Name = Str(r, 0), Table = Str(r, 1) });
        foreach (var t in list) t.Definition = await GenerateSqlAsync(con, t.Name, snap.Library, "TRIGGER", ct);
        snap.Triggers.AddRange(list);
        return list.Count;
    }

    private async Task<int> ReadProgramsAsync(DbConnection con, SchemaSnapshot snap, TimeSpan zone, CancellationToken ct)
    {
        const string sql = """
            SELECT M.PROGRAM_NAME, M.OBJECT_TYPE, M.MODULE_ATTRIBUTE, M.SOURCE_FILE_LIBRARY, M.SOURCE_FILE, M.SOURCE_FILE_MEMBER,
                   M.SOURCE_CHANGE_TIMESTAMP, M.MODULE_CREATE_TIMESTAMP,
                   M.OPTIMIZATION_LEVEL, M.DEBUG_DATA, M.SQL_COMMITMENT_CONTROL, M.SQL_NAMING, M.SQL_DATE_FORMAT, M.SQL_DEFAULT_SCHEMA
              FROM QSYS2.BOUND_MODULE_INFO M
             WHERE M.PROGRAM_LIBRARY = ?
             ORDER BY M.PROGRAM_NAME, M.OBJECT_TYPE
            """;
        var map = new Dictionary<string, ProgramObject>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
        {
            var key = $"{Str(r, 0)}|{Str(r, 1)}";
            if (map.TryGetValue(key, out var seen)) { seen.ModuleCount++; continue; }
            map[key] = new ProgramObject
            {
                Name = Str(r, 0), ObjectType = Str(r, 1), Attribute = Str(r, 2), SourceLibrary = Str(r, 3), SourceFile = Str(r, 4), SourceMember = Str(r, 5),
                SourceChanged = Utc(r[6], zone), Created = Utc(r[7], zone),
                Options = new CompileOptions
                {
                    OptimizationLevel = r[8] is DBNull ? null : SqlText.ToInt(r[8]), DebugData = r[9] is DBNull ? null : Str(r, 9) == "*YES",
                    SqlCommitment = Null(Str(r, 10)), SqlNaming = Null(Str(r, 11)), SqlDateFormat = Null(Str(r, 12)), SqlDefaultSchema = Null(Str(r, 13)),
                },
            };
        }
        const string bound = """
            SELECT PROGRAM_NAME, OBJECT_TYPE, BOUND_SERVICE_PROGRAM_LIBRARY, BOUND_SERVICE_PROGRAM
              FROM QSYS2.BOUND_SRVPGM_INFO WHERE PROGRAM_LIBRARY = ? AND BOUND_SERVICE_PROGRAM_LIBRARY <> 'QSYS'
            """;
        if (map.Count > 0)
            await foreach (var r in QueryAsync(con, bound, ct, snap.Library))
                if (map.TryGetValue($"{Str(r, 0)}|{Str(r, 1)}", out var p)) p.BoundServicePrograms.Add($"{Str(r, 2)}/{Str(r, 3)}");
        snap.Programs.AddRange(map.Values);
        return map.Count;
    }

    /// <summary>DDS display / printer / logical / physical files, commands and OPM programs (they have no bound modules).</summary>
    private async Task<int> ReadSourceObjectsAsync(DbConnection con, SchemaSnapshot snap, TimeSpan zone, CancellationToken ct)
    {
        const string sql = """
            SELECT OBJNAME, OBJTYPE, OBJATTRIBUTE, COALESCE(SOURCE_LIBRARY, ''), COALESCE(SOURCE_FILE, ''), COALESCE(SOURCE_MEMBER, ''),
                   SOURCE_TIMESTAMP, OBJCREATED, COALESCE(OBJTEXT, '')
              FROM TABLE(QSYS2.OBJECT_STATISTICS(?, '*FILE *CMD *PGM *MENU *PNLGRP')) X
             WHERE (OBJTYPE = '*FILE' AND OBJATTRIBUTE IN ('DSPF', 'PRTF', 'LF') AND SQL_OBJECT_TYPE IS NULL)
                OR OBJTYPE IN ('*CMD', '*MENU', '*PNLGRP')
                OR (OBJTYPE = '*PGM' AND OBJATTRIBUTE IN ('CLP', 'RPG', 'RPG38', 'CBL'))
            """;
        var added = new List<ProgramObject>();
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            added.Add(new ProgramObject
            {
                Name = Str(r, 0), ObjectType = Str(r, 1), Attribute = Str(r, 2), SourceLibrary = Str(r, 3), SourceFile = Str(r, 4), SourceMember = Str(r, 5),
                SourceChanged = Utc(r[6], zone), Created = Utc(r[7], zone), Text = Str(r, 8),
            });
        if (added.Any(p => p.ObjectType == "*CMD"))
            await foreach (var r in QueryAsync(con, "SELECT COMMAND_NAME, COMMAND_PROCESSING_PROGRAM_LIBRARY, COMMAND_PROCESSING_PROGRAM FROM QSYS2.COMMAND_INFO WHERE COMMAND_LIBRARY = ?", ct, snap.Library))
                if (added.FirstOrDefault(p => p.ObjectType == "*CMD" && p.Name == Str(r, 0)) is { } cmd) cmd.CommandProgram = $"{Str(r, 1)}/{Str(r, 2)}";
        snap.Programs.AddRange(added);
        return added.Count;
    }

    /// <summary>How each program / service program was created, so a rebuild can reproduce it.</summary>
    private async Task<int> ReadProgramInfoAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        if (!snap.Programs.Any(p => p.ObjectType is "*PGM" or "*SRVPGM")) return 0;
        const string sql = """
            SELECT PROGRAM_NAME, OBJECT_TYPE, ACTIVATION_GROUP, TARGET_RELEASE, USER_PROFILE, USE_ADOPTED_AUTHORITY,
                   SQL_DYNAMIC_USER_PROFILE, MODULES, EXPORT_SOURCE_LIBRARY, EXPORT_SOURCE_FILE, EXPORT_SOURCE_FILE_MEMBER
              FROM QSYS2.PROGRAM_INFO WHERE PROGRAM_LIBRARY = ?
            """;
        var n = 0;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
        {
            var p = snap.Programs.FirstOrDefault(x => x.Name == Str(r, 0) && x.ObjectType == Str(r, 1));
            if (p is null) continue;
            var o = p.Options ??= new CompileOptions();
            o.ActivationGroup = Null(Str(r, 2)); o.TargetRelease = Null(Str(r, 3)); o.UserProfile = Null(Str(r, 4));
            o.UseAdoptedAuthority = Null(Str(r, 5)); o.SqlDynamicUserProfile = Null(Str(r, 6));
            if (r[7] is not DBNull) p.ModuleCount = Math.Max(1, SqlText.ToInt(r[7], 1));
            o.ExportSourceLibrary = Null(Str(r, 8)); o.ExportSourceFile = Null(Str(r, 9)); o.ExportSourceMember = Null(Str(r, 10));
            n++;
        }
        return n;
    }

    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task<int> ReadFileReferencesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        if (!snap.Programs.Any(p => p.ObjectType is "*PGM" or "*SRVPGM")) return 0;
        // DSPPGMREF to a QTEMP outfile (QADSPPGM format). WHLNAM is the library or *LIBL; display files are included.
        await ExecAsync(con, Ddl.Qcmdexc($"DSPPGMREF PGM({snap.Library}/*ALL) OUTPUT(*OUTFILE) OBJTYPE(*PGM *SRVPGM) OUTFILE(QTEMP/TOVPGMREF)"), ct);
        var n = 0;
        await foreach (var r in QueryAsync(con, "SELECT WHPNAM, WHFNAM, WHLNAM, WHRFSN FROM QTEMP.TOVPGMREF WHERE WHOTYP = '*FILE'", ct))
            foreach (var p in snap.Programs.Where(x => x.Name.Equals(Str(r, 0), StringComparison.OrdinalIgnoreCase) && x.ObjectType is "*PGM" or "*SRVPGM"))
            {
                p.FileReferences.Add(new FileReference(Str(r, 2), Str(r, 1), Str(r, 3)));
                n++;
            }
        return n;
    }

    private async Task<int> HashSourcesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        var n = 0;
        foreach (var p in snap.Programs.Where(x => !string.IsNullOrEmpty(x.SourceFile)))
        {
            // Names come from the catalog; never interpolate one that is not a plain system name.
            if (!TurnoverOptions.IsValidLibraryName(p.SourceLibrary) || !TurnoverOptions.IsValidLibraryName(p.SourceFile) || !TurnoverOptions.IsValidLibraryName(p.SourceMember)) continue;
            // The member's source type is the truth for the compile command (an SQLRPGLE module reports attribute RPGLE).
            var type = await ScalarAsync(con,
                "SELECT COALESCE(SOURCE_TYPE, '') FROM QSYS2.SYSPARTITIONSTAT WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? AND TABLE_PARTITION = ?", ct,
                p.SourceLibrary, p.SourceFile, p.SourceMember);
            var srcType = type is null or DBNull ? null : Convert.ToString(type, System.Globalization.CultureInfo.InvariantCulture)!.Trim();
            // The member is gone (deleted, or the object was compiled from a library that does not exist on this partition):
            // record that instead of failing the whole library read.
            if (srcType is null) { p.SourceHash = ProgramObject.MissingSource; continue; }
            if (srcType.Length > 0 && p.ObjectType is "*PGM" or "*SRVPGM") p.Attribute = srcType;
            await ExecAsync(con, $"CREATE OR REPLACE ALIAS QTEMP.TOVSRC FOR {p.SourceLibrary}.{p.SourceFile} ({p.SourceMember})", ct);
            var sb = new StringBuilder();
            // A source file with CCSID 65535 comes back as bytes; hash the bytes, never "System.Byte[]".
            await foreach (var r in QueryAsync(con, "SELECT SRCDTA FROM QTEMP.TOVSRC ORDER BY SRCSEQ", ct))
                sb.Append(r[0] is byte[] raw ? Convert.ToHexString(raw) : Str(r, 0).TrimEnd()).Append('\n');
            p.SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
            n++;
        }
        return n;
    }

    private async Task<int> ReadDataAreasAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT DATA_AREA_NAME, DATA_AREA_TYPE, LENGTH, COALESCE(DECIMAL_POSITIONS, 0), DATA_AREA_VALUE, COALESCE(TEXT_DESCRIPTION, '')
              FROM QSYS2.DATA_AREA_INFO WHERE DATA_AREA_LIBRARY = ? AND SQL_SEQUENCE = 'NO'
            """;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            snap.DataAreas.Add(new DataAreaDef
            {
                Name = Str(r, 0), Type = Str(r, 1), Length = SqlText.ToInt(r[2]), Decimals = SqlText.ToInt(r[3]),
                Value = r[4] is DBNull ? null : Convert.ToString(r[4], System.Globalization.CultureInfo.InvariantCulture)?.TrimEnd(), Text = Str(r, 5),
            });
        return snap.DataAreas.Count;
    }

    private async Task<int> ReadDataQueuesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT DATA_QUEUE_NAME, MAXIMUM_MESSAGE_LENGTH, SEQUENCE, COALESCE(KEY_LENGTH, 0), COALESCE(INCLUDE_SENDER_ID, 'NO'),
                   COALESCE(TEXT_DESCRIPTION, ''), COALESCE(CURRENT_MESSAGES, 0)
              FROM QSYS2.DATA_QUEUE_INFO WHERE DATA_QUEUE_LIBRARY = ?
            """;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
        {
            var seq = Str(r, 2);
            snap.DataQueues.Add(new DataQueueDef
            {
                Name = Str(r, 0), MaxLength = SqlText.ToInt(r[1]), Sequence = seq.StartsWith('*') ? seq : "*" + seq, KeyLength = SqlText.ToInt(r[3]),
                IncludeSenderId = Str(r, 4) == "YES", Text = Str(r, 5), CurrentMessages = SqlText.ToLong(r[6]) ?? 0,
            });
        }
        return snap.DataQueues.Count;
    }

    private async Task<int> ReadJobDescriptionsAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT JOB_DESCRIPTION, COALESCE(LIBRARY_LIST, ''), COALESCE(AUTHORIZATION_NAME, '*RQD'), COALESCE(JOB_QUEUE_LIBRARY, ''),
                   COALESCE(JOB_QUEUE, ''), COALESCE(TEXT_DESCRIPTION, '')
              FROM QSYS2.JOB_DESCRIPTION_INFO WHERE JOB_DESCRIPTION_LIBRARY = ?
            """;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            snap.JobDescriptions.Add(new JobDescriptionDef
            {
                Name = Str(r, 0), LibraryList = Words(Str(r, 1)), User = Str(r, 2), JobQueue = Qualify(Str(r, 3), Str(r, 4)), Text = Str(r, 5),
            });
        return snap.JobDescriptions.Count;
    }

    private async Task<int> ReadMessagesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT MESSAGE_FILE, MESSAGE_ID, COALESCE(MESSAGE_TEXT, ''), MESSAGE_SECOND_LEVEL_TEXT, COALESCE(SEVERITY, 0)
              FROM QSYS2.MESSAGE_FILE_DATA WHERE MESSAGE_FILE_LIBRARY = ?
            """;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            snap.Messages.Add(new MessageDef { File = Str(r, 0), Id = Str(r, 1), Text = Str(r, 2), SecondLevel = r[3] is DBNull ? null : Str(r, 3), Severity = SqlText.ToInt(r[4]) });
        return snap.Messages.Count;
    }

    private async Task<int> ReadAuthoritiesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        const string sql = """
            SELECT SYSTEM_OBJECT_NAME, OBJECT_TYPE, AUTHORIZATION_NAME, OBJECT_AUTHORITY, OWNER
              FROM QSYS2.OBJECT_PRIVILEGES WHERE SYSTEM_OBJECT_SCHEMA = ?
            """;
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
        {
            var obj = Str(r, 0); var type = Str(r, 1); var user = Str(r, 2);
            // The owner's own *ALL row is implied by ownership; recording it as *OWNER keeps it out of the grant list.
            if (owners.Add($"{obj}|{type}")) snap.Authorities.Add(new ObjectAuthorityDef { Object = obj, ObjectType = type, User = "*OWNER", Authority = Str(r, 4) });
            if (user.Equals(Str(r, 4), StringComparison.OrdinalIgnoreCase)) continue;
            snap.Authorities.Add(new ObjectAuthorityDef { Object = obj, ObjectType = type, User = user, Authority = Str(r, 3) });
        }
        return snap.Authorities.Count;
    }

    private async Task<int> ReadBindingDirectoriesAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        // OBJECT_STATISTICS first: an empty binding directory has no rows in BINDING_DIRECTORY_INFO.
        var dirs = new Dictionary<string, BindingDirectoryDef>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in QueryAsync(con, "SELECT OBJNAME, COALESCE(OBJTEXT, '') FROM TABLE(QSYS2.OBJECT_STATISTICS(?, '*BNDDIR')) X", ct, snap.Library))
            dirs[Str(r, 0)] = new BindingDirectoryDef { Name = Str(r, 0), Text = Str(r, 1) };
        if (dirs.Count == 0) return 0;
        const string sql = """
            SELECT BINDING_DIRECTORY, COALESCE(ENTRY_LIBRARY, '*LIBL'), ENTRY, ENTRY_TYPE, COALESCE(ENTRY_ACTIVATION, '*IMMED')
              FROM QSYS2.BINDING_DIRECTORY_INFO WHERE BINDING_DIRECTORY_LIBRARY = ?
            """;
        await foreach (var r in QueryAsync(con, sql, ct, snap.Library))
            if (dirs.TryGetValue(Str(r, 0), out var d))
                d.Entries.Add(new BindingEntryDef { Library = Str(r, 1), Object = Str(r, 2), Type = Str(r, 3), Activation = Str(r, 4) });
        snap.BindingDirectories.AddRange(dirs.Values);
        return dirs.Count;
    }

    private async Task<int> ReadKeyMaximaAsync(DbConnection con, SchemaSnapshot snap, CancellationToken ct)
    {
        var targets = snap.Tables.SelectMany(t => t.Columns.Where(c => c.IsIdentity).Select(c => (t, c))).ToList();
        foreach (var b in options.SequenceBindings.Where(x => x.Logical.Equals(snap.Logical, StringComparison.OrdinalIgnoreCase)))
            if (snap.Table(b.Table) is { } t && t.Column(b.Column) is { } c) targets.Add((t, c));
        foreach (var (t, c) in targets.Distinct())
        {
            if (!TurnoverOptions.IsValidLibraryName(c.SystemName) || !TurnoverOptions.IsValidLibraryName(t.SystemName)) continue;
            c.MaxValue = SqlText.ToLong(await ScalarAsync(con, $"SELECT CAST(MAX({c.SystemName}) AS VARCHAR(64)) FROM {snap.Library}.{t.SystemName}", ct));
            // The identity generator's next value (documented by IBM as QSYS2/SYSPSTAT.NEXT_IDENTITY_VALUE): without it the
            // "identity behind MAX(key)" check (COL009) could never fire on a live system.
            if (c.IsIdentity)
                c.IdentityNextValue = SqlText.ToLong(await ScalarAsync(con,
                    "SELECT CAST(MAX(NEXT_IDENTITY_VALUE) AS VARCHAR(40)) FROM QSYS2.SYSPSTAT WHERE TABSCHEMA = ? AND TABNAME = ?", ct, snap.Library, t.Name));   // TABNAME is the SQL (long) name
        }
        return targets.Count;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// One CREATE statement for a routine, view or trigger. GRANT / LABEL / COMMENT output is switched off (it would be
    /// run as part of the same statement and fail), the text is read from QTEMP.Q_GENSQL in record order, and anything
    /// before the CREATE (SET PATH, SET OPTION) is dropped.
    /// </summary>
    private async Task<string> GenerateSqlAsync(DbConnection con, string name, string lib, string type, CancellationToken ct)
    {
        const string call = "CALL QSYS2.GENERATE_SQL(?, ?, ?, CREATE_OR_REPLACE_OPTION => '1', HEADER_OPTION => '0', STATEMENT_FORMATTING_OPTION => '1', " +
                            "PRIVILEGES_OPTION => '0', LABEL_OPTION => '0', COMMENT_OPTION => '0')";
        await ExecAsync(con, call, ct, name, lib, type);
        var sb = new StringBuilder();
        await foreach (var r in QueryAsync(con, "SELECT SRCDTA FROM QTEMP.Q_GENSQL ORDER BY SRCSEQ", ct)) sb.Append(Str(r, 0)).Append('\n');
        return CreateStatement(sb.ToString());
    }

    internal static string CreateStatement(string generated)
    {
        var m = System.Text.RegularExpressions.Regex.Match(generated, @"^\s*CREATE\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        var text = m.Success ? generated[m.Index..] : generated;
        return text.Trim().TrimEnd(';').Trim();
    }

    /// <summary>CCSID 65535 columns arrive as raw EBCDIC; CCSID 37 (US/Canada) is the usual source CCSID.</summary>
    private static readonly Encoding Ebcdic = CreateEbcdic();
    private static Encoding CreateEbcdic()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(37);
    }

    /// <summary>USER_INFO.SUPPLEMENTAL_GROUP_LIST: up to 15 names, each padded to 10 characters, no separator.</summary>
    internal static List<string> FixedWidth(string s, int width) =>
        Enumerable.Range(0, (s.Length + width - 1) / width).Select(i => s.Substring(i * width, Math.Min(width, s.Length - i * width)).Trim())
            .Where(x => x.Length > 0).ToList();

    private static List<string> Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    private static string Qualify(string lib, string obj) => string.IsNullOrEmpty(lib) || obj.StartsWith('*') ? obj : $"{lib}/{obj}";
    private static string Str(DbDataReader r, int i) => r[i] switch
    {
        DBNull => "",
        byte[] raw => Ebcdic.GetString(raw).Trim('\0', ' '),   // CCSID 65535 text column: untranslated EBCDIC bytes
        var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!.Trim(),
    };

    private async Task<object?> ScalarAsync(DbConnection con, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Command(con, sql, args);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private async Task ExecAsync(DbConnection con, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Command(con, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async IAsyncEnumerable<DbDataReader> QueryAsync(DbConnection con, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, params object[] args)
    {
        await using var cmd = Command(con, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader;
    }

    private DbCommand Command(DbConnection con, string sql, params object[] args)
    {
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Timeout;
        foreach (var a in args)
        {
            var p = cmd.CreateParameter();
            p.Value = a;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
