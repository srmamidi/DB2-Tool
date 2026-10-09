using System.Collections.Concurrent;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Catalog;

/// <summary>
/// A realistic order-entry application spread over Dev / QA / Stage / Prod, with the kinds of drift
/// real IBM i shops have: an emergency fix made in QA, a table created without FOR SYSTEM NAME, a
/// sequence that fell behind copied data, a prod-only performance index, hard-coded libraries in
/// procedures, and a cross-library foreign key to a library nobody mapped.
/// Expects logical libraries CUSTOMER, ORDERS, ORDPGM, ORDSRC (any that are missing are skipped).
/// </summary>
public sealed class SampleCatalogProvider : ICatalogProvider
{
    private readonly ConcurrentDictionary<string, Dictionary<string, SchemaSnapshot>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<SchemaSnapshot?> GetSnapshotAsync(TurnoverEnvironment env, string library, CatalogTrace trace, CancellationToken ct = default) =>
        trace.Run(library, "Sample catalog", () =>
        {
            var all = _cache.GetOrAdd(env.Key, _ => SampleData.Build(env));
            return Task.FromResult(all.GetValueOrDefault(library));
        }, s => s is null ? 0 : s.Tables.Count + s.Routines.Count + s.Programs.Count);

    public Task<IReadOnlyList<UserProfileDef>> GetProfilesAsync(TurnoverEnvironment env, IReadOnlyCollection<string> names, IReadOnlyCollection<string> patterns, CatalogTrace trace, CancellationToken ct = default) =>
        trace.Run("*SYSTEM", "User profiles (sample)", () => Task.FromResult<IReadOnlyList<UserProfileDef>>(SampleData.Profiles(env)
            .Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase) || patterns.Any(t => t.EndsWith('*') ? p.Name.StartsWith(t[..^1], StringComparison.OrdinalIgnoreCase) : p.Name.Equals(t, StringComparison.OrdinalIgnoreCase)))
            .ToList()), l => l.Count);

    public Task<SystemObjects> GetSystemObjectsAsync(TurnoverEnvironment env, TurnoverOptions options, CatalogTrace trace, CancellationToken ct = default) =>
        trace.Run("*SYSTEM", "System objects (sample)", () => Task.FromResult(SampleData.SystemObjects(env, options)),
            s => s.AuthorizationLists.Count + s.JobSchedules.Count + s.SystemValues.Count + s.IfsFiles.Count);

    public Task<IReadOnlyList<string>?> GetSourceAsync(TurnoverEnvironment env, string library, string file, string member, CancellationToken ct = default)
    {
        var all = _cache.GetOrAdd(env.Key, _ => SampleData.Build(env));
        var p = all.Values.SelectMany(s => s.Programs).FirstOrDefault(x =>
            x.SourceLibrary.Equals(library, StringComparison.OrdinalIgnoreCase) && x.SourceFile.Equals(file, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(x.SourceMember) ? x.Name : x.SourceMember).Equals(member, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<string>?>(p is null ? null : SampleData.SourceText(p));
    }

    public Task<IReadOnlyList<(string Check, bool Ok, string Message)>> TestAsync(TurnoverEnvironment env, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, bool, string)>>([("Sample catalog", true, $"Built-in demo data for {env.Key}. Nothing is sent to IBM i.")]);
}

internal static class SampleData
{
    private static readonly DateTimeOffset Base = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Partition-wide sample objects, filtered by the Tracked* settings like the live reader.</summary>
    public static SystemObjects SystemObjects(TurnoverEnvironment env, TurnoverOptions o)
    {
        var lv = 3 - Math.Clamp((int)env.Tier, 0, 3);
        var pgm = env.PhysicalFor("ORDPGM") ?? "QGPL";
        static bool Tracked(IEnumerable<string> list, string name) =>
            list.Any(t => t.EndsWith('*') ? name.StartsWith(t[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(t, StringComparison.OrdinalIgnoreCase));
        var s = new Models.SystemObjects();

        var autl = new AuthorizationListDef { Name = "ORDAUTL", Owner = "QSECOFR" };
        autl.Entries.Add(new AuthorizationListEntry { User = "*PUBLIC", Authority = "*EXCLUDE" });
        autl.Entries.Add(new AuthorizationListEntry { User = "ORDUSERS", Authority = "*USE" });
        if (lv >= 3) autl.Entries.Add(new AuthorizationListEntry { User = "ORDSUPV", Authority = "*CHANGE" });
        if (Tracked(o.TrackedAuthorizationLists, autl.Name)) s.AuthorizationLists.Add(autl);

        var nightly = new JobScheduleDef
        {
            Name = "ORDNIGHTLY", EntryNumber = 100 + (int)env.Tier, Status = env.Tier == EnvironmentTier.QA ? "HELD" : "SCHEDULED", Frequency = "*WEEKLY",
            ScheduledDays = "*ALL", ScheduledTime = lv >= 3 ? "23:30:00" : "22:00:00",
            Command = lv >= 3 ? $"CALL PGM({pgm}/ORDRPT) PARM('NIGHT')" : $"CALL PGM({pgm}/ORDRPT)",
            JobDescription = $"{pgm}/ORDJOBD", JobQueue = "QGPL/QBATCH", User = "*CURRENT", Text = "Nightly open-order report",
        };
        if (Tracked(o.TrackedJobSchedules, nightly.Name)) s.JobSchedules.Add(nightly);

        foreach (var (name, value, numeric) in new[] { ("QDATFMT", "MDY", false), ("QCCSID", "37", true), ("QMAXSIGN", lv >= 3 ? "5" : "3", true), ("QTIMZON", "QN0500EST", false) })
            if (Tracked(o.TrackedSystemValues, name)) s.SystemValues.Add(new SystemValueDef { Name = name, Value = value, Numeric = numeric });

        if (env.IfsPaths.TryGetValue("WEB", out var root))
        {
            void File(string rel, long size, string hash) => s.IfsFiles.Add(new IfsFileDef
            {
                Logical = "WEB", RelativePath = rel, Path = root.TrimEnd('/') + "/" + rel, Size = size, Hash = hash, Ccsid = 1208, Changed = Base.AddDays((int)env.Tier),
            });
            File("index.html", 4_210, "A1B2C3D4E5F60718");
            File("img/logo.png", 18_442, "0F1E2D3C4B5A6978");
            File("conf/app.json", lv >= 3 ? 912 : 870, lv >= 3 ? "C0FFEE0000000002" : "C0FFEE0000000001");
            if (lv >= 3) File("conf/shipping.json", 340, "5A5A5A5A00000001");
            if (env.Tier == EnvironmentTier.Prod) File("conf/server.key", 1_704, "BEEF000000000001");
        }
        return s;
    }

    public static Dictionary<string, SchemaSnapshot> Build(TurnoverEnvironment env)
    {
        // How far ahead of production this environment is: Dev has every pending change.
        var lv = 3 - Math.Clamp((int)env.Tier, 0, 3);
        var isQa = env.Tier == EnvironmentTier.QA;
        var isStage = env.Tier == EnvironmentTier.Stage;
        var isProd = env.Tier == EnvironmentTier.Prod;
        string? cus = env.PhysicalFor("CUSTOMER"), ord = env.PhysicalFor("ORDERS"), pgm = env.PhysicalFor("ORDPGM"), src = env.PhysicalFor("ORDSRC");
        var result = new Dictionary<string, SchemaSnapshot>(StringComparer.OrdinalIgnoreCase);
        SchemaSnapshot Snap(string lib, string logical) => result[lib] = new SchemaSnapshot { EnvironmentKey = env.Key, Library = lib, Logical = logical };

        long custRows = env.Tier switch { EnvironmentTier.Dev => 300, EnvironmentTier.QA => 5_120, EnvironmentTier.Stage => 40_000, _ => 81_234 };
        long orderRows = env.Tier switch { EnvironmentTier.Dev => 800, EnvironmentTier.QA => 90_550, EnvironmentTier.Stage => 1_200_000, _ => 2_400_000 };

        // ------------------------------------------------------------------ CUSTOMER
        var customer = new TableDef
        {
            Name = "CUSTOMER", SystemName = "CUSTMAST", Text = "Customer master", RowCount = custRows, Journaled = true, JournalName = "QSQJRN",
            PrimaryKey = ["CUST_ID"], PrimaryKeyName = "CUSTOMER_PK",
            Columns =
            [
                new() { Name = "CUST_ID", SystemName = "CUST_ID", DataType = "INTEGER", Nullable = false, Ordinal = 1, IsIdentity = true,
                        MaxValue = custRows, IdentityNextValue = isQa ? 5_001 : custRows + 1 },
                new() { Name = "NAME", SystemName = "NAME", DataType = "VARCHAR", Length = 100, Nullable = false, Ordinal = 2 },
                new() { Name = "EMAIL", SystemName = "EMAIL", DataType = "VARCHAR", Length = lv >= 2 ? 254 : 120, Ordinal = 3 },
                lv >= 3 ? new() { Name = "STATUS", SystemName = "STATUS", DataType = "CHAR", Length = 1, Nullable = false, Default = "'A'", Ordinal = 4 }
                        : new() { Name = "STATUS", SystemName = "STATUS", DataType = "CHAR", Length = 1, Ordinal = 4 },
                new() { Name = "CREATED", SystemName = "CREATED", DataType = "TIMESTAMP", Nullable = false, Default = "CURRENT_TIMESTAMP", Ordinal = 5 },
            ],
        };
        if (lv >= 1) customer.Columns.Add(new() { Name = "PHONE", SystemName = "PHONE", DataType = "VARCHAR", Length = 20, Ordinal = 6 });
        if (lv >= 3) customer.Columns.Add(new() { Name = "LOYALTY_TIER", SystemName = "LOYALTIER", DataType = "CHAR", Length = 1, Nullable = false, Ordinal = 7 });
        customer.FormatLevelId = customer.ComputeFormatLevel();

        if (cus is not null)
        {
            var s = Snap(cus, "CUSTOMER");
            s.Tables.Add(customer);
            s.Indexes.Add(new IndexDef { Name = "CUSTOMER_EMAIL_IX", SystemName = "CUSTEMLIX", Table = "CUSTOMER", Columns = [new("EMAIL")] });
        }

        // ------------------------------------------------------------------ ORDERS
        var header = new TableDef
        {
            Name = "ORDER_HEADER", SystemName = "ORDHDR", Text = "Order header", RowCount = orderRows, Journaled = true, JournalName = "QSQJRN",
            PrimaryKey = ["ORDER_ID"], PrimaryKeyName = "ORDHDR_PK",
            Columns =
            [
                new() { Name = "ORDER_ID", SystemName = "ORDER_ID", DataType = "BIGINT", Nullable = false, Ordinal = 1, MaxValue = orderRows },
                new() { Name = "CUST_ID", SystemName = "CUST_ID", DataType = "INTEGER", Nullable = false, Ordinal = 2 },
                new() { Name = "ORDER_DATE", SystemName = "ORDDATE", DataType = "DATE", Nullable = false, Ordinal = 3 },
                new() { Name = "TOTAL", SystemName = "TOTAL", DataType = "DECIMAL", Length = lv >= 3 ? 9 : 11, Scale = 2, Nullable = false, Default = "0", Ordinal = 4 },
                lv >= 3 ? new() { Name = "ORDER_STATUS", SystemName = "ORDSTS", DataType = "CHAR", Length = 2, Nullable = false, Default = "'OP'", Ordinal = 5 }
                        : new() { Name = "STATUS", SystemName = "STATUS", DataType = "CHAR", Length = 2, Nullable = false, Default = "'OP'", Ordinal = 5 },
            ],
        };
        if (lv >= 3)
        {
            header.Columns.Add(new() { Name = "SHIP_ID", SystemName = "SHIP_ID", DataType = "INTEGER", Ordinal = 6 });
            header.Columns.Add(new() { Name = "SHIP_METHOD", SystemName = "SHIPMTH", DataType = "CHAR", Length = 3, Nullable = false, Default = "'GND'", Ordinal = 7 });
            header.Columns.Add(new() { Name = "EXT_REF", SystemName = "EXT_REF", DataType = "VARCHAR", Length = 40, Ordinal = 8 });
        }
        header.FormatLevelId = header.ComputeFormatLevel();

        var line = new TableDef
        {
            Name = "ORDER_LINE", SystemName = isQa ? "ORDER00001" : "ORDLIN", Text = "Order lines", RowCount = orderRows * 4,
            Journaled = !isStage, JournalName = isStage ? null : "QSQJRN",
            PrimaryKey = ["ORDER_ID", "LINE_NO"], PrimaryKeyName = "ORDLIN_PK",
            Columns =
            [
                new() { Name = "ORDER_ID", SystemName = "ORDER_ID", DataType = "BIGINT", Nullable = false, Ordinal = 1 },
                new() { Name = "LINE_NO", SystemName = "LINE_NO", DataType = "SMALLINT", Nullable = false, Ordinal = 2 },
                new() { Name = "ITEM", SystemName = "ITEM", DataType = "CHAR", Length = 15, Nullable = false, Ordinal = 3 },
                new() { Name = "QTY", SystemName = "QTY", DataType = "DECIMAL", Length = 7, Scale = 0, Nullable = false, Ordinal = 4 },
                new() { Name = "PRICE", SystemName = "PRICE", DataType = "DECIMAL", Length = 9, Scale = 2, Nullable = false, Ordinal = 5 },
            ],
        };
        line.FormatLevelId = line.ComputeFormatLevel();

        if (ord is not null)
        {
            var s = Snap(ord, "ORDERS");
            s.Tables.AddRange([header, line]);
            s.Indexes.Add(new IndexDef { Name = "ORDHDR_CUST_IX", SystemName = "ORDHDRCIX", Table = "ORDER_HEADER",
                Columns = lv >= 3 ? [new("CUST_ID"), new("ORDER_DATE", true)] : [new("CUST_ID")] });
            if (lv >= 1)
                s.Indexes.Add(new IndexDef { Name = isQa ? "ORDLIN_ITEM_IX0" : "ORDLIN_ITEM_IX", SystemName = isQa ? "ORDLINIIX0" : "ORDLINIIX", Table = "ORDER_LINE", Columns = [new("ITEM")] });
            if (isProd)
                s.Indexes.Add(new IndexDef { Name = "PRD_ORDHDR_DATE_IX", SystemName = "PRDOHDTIX", Table = "ORDER_HEADER", Columns = [new("ORDER_DATE", true)] });

            s.ForeignKeys.Add(new ForeignKeyDef { Name = "ORDHDR_CUST_FK", Table = "ORDER_HEADER", Columns = ["CUST_ID"], RefLibrary = cus ?? "CUSTOMER", RefTable = "CUSTOMER", RefColumns = ["CUST_ID"] });
            s.ForeignKeys.Add(new ForeignKeyDef { Name = "ORDLIN_ORDHDR_FK", Table = "ORDER_LINE", Columns = ["ORDER_ID"], RefLibrary = ord, RefTable = "ORDER_HEADER", RefColumns = ["ORDER_ID"], DeleteRule = lv >= 3 ? "CASCADE" : "RESTRICT" });

            s.Sequences.Add(new SequenceDef { Name = "ORDER_SEQ", DataType = "BIGINT", Start = 1, Increment = 1, Cache = lv >= 3 ? 20 : 50,
                NextValue = isQa ? 90_012 : orderRows + 1, BoundTable = "ORDER_HEADER", BoundColumn = "ORDER_ID" });

            s.Routines.Add(new RoutineDef
            {
                Name = "FN_ORDER_AGE", SpecificName = "FN_ORDER_AGE", Kind = "FUNCTION", Parameters = ["P_DATE DATE"],
                Definition = $"CREATE OR REPLACE FUNCTION {ord}.FN_ORDER_AGE (P_DATE DATE)\n  RETURNS INTEGER\n  LANGUAGE SQL\n  SPECIFIC {ord}.FN_ORDER_AGE\n  DETERMINISTIC\n  RETURN DAYS(CURRENT DATE) - DAYS(P_DATE)",
            });
            s.Routines.Add(new RoutineDef
            {
                Name = "SP_CREATE_ORDER", SpecificName = "SP_CREATE_ORDER", Parameters = ["IN P_CUST_ID INTEGER", "OUT P_ORDER_ID BIGINT"],
                Definition = lv >= 3
                    ? $"""
                      CREATE OR REPLACE PROCEDURE {ord}.SP_CREATE_ORDER (
                        IN P_CUST_ID INTEGER,
                        OUT P_ORDER_ID BIGINT)
                        LANGUAGE SQL
                        SPECIFIC {ord}.SP_CREATE_ORDER
                      BEGIN
                        DECLARE V_STATUS CHAR(1);
                        SELECT STATUS INTO V_STATUS FROM {cus}.CUSTOMER WHERE CUST_ID = P_CUST_ID;
                        IF V_STATUS <> 'A' THEN
                          SIGNAL SQLSTATE '75001' SET MESSAGE_TEXT = 'Customer is not active';
                        END IF;
                        SET P_ORDER_ID = NEXT VALUE FOR {ord}.ORDER_SEQ;
                        INSERT INTO {ord}.ORDER_HEADER (ORDER_ID, CUST_ID, ORDER_DATE, TOTAL, ORDER_STATUS, SHIP_METHOD)
                          VALUES (P_ORDER_ID, P_CUST_ID, CURRENT DATE, 0, 'OP', 'GND');
                        -- legacy history feed (copied from the old batch job)
                        INSERT INTO ORDPRD.ORDER_HISTORY (ORDER_ID, EVENT) VALUES (P_ORDER_ID, 'CREATED');
                        CALL QSYS2.QCMDEXC('SNDDTAQ DTAQ({ord}/ORDQ) LEN(10) DATA(''NEWORDER'')');
                      END
                      """
                    : $"""
                      CREATE OR REPLACE PROCEDURE {ord}.SP_CREATE_ORDER (
                        IN P_CUST_ID INTEGER,
                        OUT P_ORDER_ID BIGINT)
                        LANGUAGE SQL
                        SPECIFIC {ord}.SP_CREATE_ORDER
                      BEGIN
                        SET P_ORDER_ID = NEXT VALUE FOR ORDER_SEQ;
                        INSERT INTO ORDER_HEADER (ORDER_ID, CUST_ID, ORDER_DATE, TOTAL, STATUS)
                          VALUES (P_ORDER_ID, P_CUST_ID, CURRENT DATE, 0, 'OP');
                      END
                      """,
            });
            s.Routines.Add(lv >= 3
                ? new RoutineDef
                {
                    Name = "SP_ORDER_TOTAL", SpecificName = "SP_ORDER_TOTAL_V2", Language = "RPGLE", ExternalName = $"{pgm}/ORDTOTR",
                    Parameters = ["IN P_ORDER_ID BIGINT", "IN P_INCLUDE_TAX CHAR(1)", "OUT P_TOTAL DECIMAL(9,2)"],
                    Definition = $"CREATE OR REPLACE PROCEDURE {ord}.SP_ORDER_TOTAL (\n  IN P_ORDER_ID BIGINT,\n  IN P_INCLUDE_TAX CHAR(1),\n  OUT P_TOTAL DECIMAL(9, 2))\n  LANGUAGE RPGLE\n  SPECIFIC {ord}.SP_ORDER_TOTAL_V2\n  PARAMETER STYLE GENERAL\n  EXTERNAL NAME '{pgm}/ORDTOTR'",
                }
                : new RoutineDef
                {
                    Name = "SP_ORDER_TOTAL", SpecificName = "SP_ORDER_TOTAL", Language = "RPGLE", ExternalName = $"{pgm}/ORDTOTR",
                    Parameters = ["IN P_ORDER_ID BIGINT", "OUT P_TOTAL DECIMAL(11,2)"],
                    Definition = $"CREATE OR REPLACE PROCEDURE {ord}.SP_ORDER_TOTAL (\n  IN P_ORDER_ID BIGINT,\n  OUT P_TOTAL DECIMAL(11, 2))\n  LANGUAGE RPGLE\n  SPECIFIC {ord}.SP_ORDER_TOTAL\n  PARAMETER STYLE GENERAL\n  EXTERNAL NAME '{pgm}/ORDTOTR'",
                });

            var status = lv >= 3 ? "ORDER_STATUS" : "STATUS";
            s.Views.Add(new ViewDef
            {
                Name = "V_OPEN_ORDERS", SystemName = "VOPENORD", References = [$"{ord}.ORDER_HEADER", $"{cus}.CUSTOMER"],
                Definition = $"CREATE OR REPLACE VIEW {ord}.V_OPEN_ORDERS (ORDER_ID, CUST_ID, NAME, TOTAL, AGE_DAYS) AS\nSELECT H.ORDER_ID, H.CUST_ID, C.NAME, H.TOTAL, {ord}.FN_ORDER_AGE(H.ORDER_DATE)\n  FROM {ord}.ORDER_HEADER H\n  JOIN {cus}.CUSTOMER C ON C.CUST_ID = H.CUST_ID\n WHERE H.{status} = 'OP'",
            });
            s.Views.Add(new ViewDef
            {
                Name = "V_ORDER_LINES", SystemName = "VORDLIN", References = [$"{ord}.ORDER_LINE"],
                Definition = $"CREATE OR REPLACE VIEW {ord}.V_ORDER_LINES AS\nSELECT L.* FROM {ord}.ORDER_LINE L",
            });

            if (lv >= 3)
            {
                s.Tables.Add(new TableDef
                {
                    Name = "SHIPPER", SystemName = "SHIPPER", Text = "Carriers", RowCount = 6, Journaled = true, PrimaryKey = ["SHIP_ID"], PrimaryKeyName = "SHIPPER_PK",
                    Columns = [new() { Name = "SHIP_ID", SystemName = "SHIP_ID", DataType = "INTEGER", Nullable = false, Ordinal = 1 },
                               new() { Name = "NAME", SystemName = "NAME", DataType = "VARCHAR", Length = 60, Nullable = false, Ordinal = 2 }],
                });
                s.Tables.Add(new TableDef
                {
                    Name = "ORDER_AUDIT", SystemName = "ORDAUD", Text = "Order status audit", RowCount = 120, Journaled = true, PrimaryKey = ["AUDIT_ID"], PrimaryKeyName = "ORDAUD_PK",
                    Columns = [new() { Name = "AUDIT_ID", SystemName = "AUDIT_ID", DataType = "BIGINT", Nullable = false, Ordinal = 1 },
                               new() { Name = "ORDER_ID", SystemName = "ORDER_ID", DataType = "BIGINT", Nullable = false, Ordinal = 2 },
                               new() { Name = "USER_ID", SystemName = "USER_ID", DataType = "INTEGER", Ordinal = 3 },
                               new() { Name = "ACTION", SystemName = "ACTION", DataType = "VARCHAR", Length = 20, Nullable = false, Ordinal = 4 },
                               new() { Name = "AT_TS", SystemName = "AT_TS", DataType = "TIMESTAMP", Nullable = false, Default = "CURRENT_TIMESTAMP", Ordinal = 5 }],
                });
                s.Indexes.Add(new IndexDef { Name = "ORDHDR_EXTREF_UX", SystemName = "ORDHDRXUX", Table = "ORDER_HEADER", IsUnique = true, Columns = [new("EXT_REF")] });
                s.ForeignKeys.Add(new ForeignKeyDef { Name = "ORDHDR_SHIP_FK", Table = "ORDER_HEADER", Columns = ["SHIP_ID"], RefLibrary = ord, RefTable = "SHIPPER", RefColumns = ["SHIP_ID"] });
                s.ForeignKeys.Add(new ForeignKeyDef { Name = "ORDAUD_ORD_FK", Table = "ORDER_AUDIT", Columns = ["ORDER_ID"], RefLibrary = ord, RefTable = "ORDER_HEADER", RefColumns = ["ORDER_ID"], DeleteRule = "CASCADE" });
                s.ForeignKeys.Add(new ForeignKeyDef { Name = "ORDAUD_USR_FK", Table = "ORDER_AUDIT", Columns = ["USER_ID"], RefLibrary = "SECDEV", RefTable = "APP_USER", RefColumns = ["USER_ID"] });
                s.Sequences.Add(new SequenceDef { Name = "AUDIT_SEQ", DataType = "BIGINT", Start = 1, Increment = 1, Cache = 20, NextValue = 121, BoundTable = "ORDER_AUDIT", BoundColumn = "AUDIT_ID" });
                s.Routines.Add(new RoutineDef
                {
                    Name = "SP_SHIP_LABEL", SpecificName = "SP_SHIP_LABEL", Language = "RPGLE", ExternalName = $"{pgm}/SHPLBLR", Parameters = ["IN P_ORDER_ID BIGINT"],
                    Definition = $"CREATE OR REPLACE PROCEDURE {ord}.SP_SHIP_LABEL (\n  IN P_ORDER_ID BIGINT)\n  LANGUAGE RPGLE\n  SPECIFIC {ord}.SP_SHIP_LABEL\n  PARAMETER STYLE GENERAL\n  EXTERNAL NAME '{pgm}/SHPLBLR'",
                });
                s.Triggers.Add(new TriggerDef
                {
                    Name = "ORDHDR_AUDIT_TRG", Table = "ORDER_HEADER",
                    Definition = $"CREATE OR REPLACE TRIGGER {ord}.ORDHDR_AUDIT_TRG\n  AFTER UPDATE OF ORDER_STATUS ON {ord}.ORDER_HEADER\n  REFERENCING NEW AS N\n  FOR EACH ROW MODE DB2ROW\n  INSERT INTO {ord}.ORDER_AUDIT (AUDIT_ID, ORDER_ID, ACTION)\n  VALUES (NEXT VALUE FOR {ord}.AUDIT_SEQ, N.ORDER_ID, 'STATUS')",
                });
            }
            if (isQa)
                s.Tables.Add(new TableDef
                {
                    Name = "TMP_FIX_0412", SystemName = "TMPFIX0412", Text = "One-off fix", RowCount = 37,
                    Columns = [new() { Name = "ORDER_ID", SystemName = "ORDER_ID", DataType = "BIGINT", Ordinal = 1 },
                               new() { Name = "NOTE", SystemName = "NOTE", DataType = "VARCHAR", Length = 200, Ordinal = 2 }],
                });
        }

        // ------------------------------------------------------------------ programs & source
        if (pgm is not null && src is not null)
        {
            var s = Snap(pgm, "ORDPGM");
            Snap(src, "ORDSRC");
            FileReference F(TableDef t) => new("*LIBL", t.SystemName, t.FormatLevelId);
            ProgramObject P(string name, string type, string attr, string hash, DateTimeOffset srcChg, DateTimeOffset created, string text, params FileReference[] files) => new()
            {
                Name = name, ObjectType = type, Attribute = attr, SourceLibrary = src, SourceFile = "QRPGLESRC", SourceMember = name,
                SourceHash = hash, SourceChanged = srcChg, Created = created, Text = text, FileReferences = files.ToList(),
            };

            var utilChanged = lv >= 3 ? Base.AddDays(202) : isQa ? Base.AddDays(210) : Base;
            s.Programs.Add(P("ORDUTIL", "*SRVPGM", "RPGLE", lv >= 3 ? "u2-tax-rounding" : isQa ? "u1-hotfix-0928" : "u1", utilChanged, utilChanged.AddMinutes(15), "Order utilities"));
            var entr = P("ORDENTR", "*PGM", "RPGLE", lv >= 3 ? "e2-shipmethod" : "e1", lv >= 3 ? Base.AddDays(200) : Base, (lv >= 3 ? Base.AddDays(200) : Base).AddMinutes(10),
                "Order entry", F(header), F(customer), F(line));
            entr.BoundServicePrograms.Add($"{pgm}/ORDUTIL");
            s.Programs.Add(entr);
            s.Programs.Add(P("ORDTOTR", "*PGM", "SQLRPGLE", "t1", Base, Base.AddMinutes(5), "Order total (SP_ORDER_TOTAL)", F(header), F(line)));
            s.Programs.Add(P("ORDRPT", "*PGM", "RPGLE", lv >= 3 ? "r2" : "r1", lv >= 3 ? Base.AddDays(205) : Base, lv >= 3 ? Base.AddDays(190) : Base.AddMinutes(5), "Open order report", F(header)));
            s.Programs.Add(P("CUSMNT", "*PGM", "RPGLE", "c1", Base, Base.AddMinutes(5), "Customer maintenance", F(customer)));
            if (lv >= 3)
                s.Programs.Add(P("SHPLBLR", "*PGM", "RPGLE", "s1", Base.AddDays(204), Base.AddDays(204).AddMinutes(3), "Shipping label",
                    new FileReference(ord ?? "*LIBL", "ORDHDR", header.FormatLevelId), new FileReference("*LIBL", "SHIPPER", "")));
        }
        Extras(env, lv, result, cus, ord, pgm, src, header, line, customer);
        return result;
    }

    /// <summary>Plausible source whose content follows the object's source hash, so versions really differ.</summary>
    public static IReadOnlyList<string> SourceText(ProgramObject p)
    {
        var lines = new List<string>();
        if (p.ObjectType == "*FILE")
        {
            lines.Add($"     A*  {p.Name} - {p.Text}");
            lines.Add("     A                                      DSPSIZ(24 80 *DS3)");
            lines.Add("     A          R ENTRY");
            lines.Add("     A                                  1 30'Order entry'");
            lines.Add("     A            ORDER_ID      15S 0B  4 20");
            if (p.SourceHash.Contains("2")) lines.Add("     A            SHIPMTH        3A  B  6 20");
            return lines;
        }
        if (p.ObjectType == "*CMD") return [$"             CMD        PROMPT('{p.Text}')", "             PARM       KWD(ORDER) TYPE(*DEC) LEN(15 0) MIN(1)"];
        lines.Add("**free");
        lines.Add($"ctl-opt dftactgrp(*no) actgrp(*caller);  // {p.Name}: {p.Text}");
        foreach (var f in p.FileReferences) lines.Add($"dcl-f {f.File} usage(*input) keyed;");
        lines.Add("dcl-s orderId packed(15:0);");
        lines.Add("");
        lines.Add("read ORDHDR;");
        if (p.SourceHash.Contains("2") || p.SourceHash.Contains("3")) { lines.Add("// ship method is now captured at entry"); lines.Add("shipMethod = 'GND';"); }
        if (p.SourceHash.Contains("hotfix")) { lines.Add("// HOTFIX 09-28: round tax to 2 decimals"); lines.Add("tax = %dech(tax: 9: 2);"); }
        if (p.SourceHash.Contains("notes")) lines.Add("note = %trim(noteIn);");
        lines.Add("*inlr = *on;");
        lines.Add("return;");
        return lines;
    }

    private static string Sfx(TurnoverEnvironment env) => env.Tier switch { EnvironmentTier.Dev => "DEV", EnvironmentTier.QA => "QA", EnvironmentTier.Stage => "STG", _ => "PRD" };

    /// <summary>Service accounts and groups. Passwords are never part of a profile snapshot.</summary>
    public static List<UserProfileDef> Profiles(TurnoverEnvironment env)
    {
        var sfx = Sfx(env);
        var pgm = env.PhysicalFor("ORDPGM") ?? "QGPL";
        var list = new List<UserProfileDef>
        {
            new() { Name = "ORDUSERS", UserClass = "*USER", Text = "Order entry users (group)", NoPassword = true, InitialMenu = "*SIGNOFF" },
            new()
            {
                Name = "SVCORD" + sfx, Text = "Order service account", NoPassword = true, GroupProfile = "ORDUSERS",
                CurrentLibrary = env.PhysicalFor("ORDERS") ?? "*CRTDFT", JobDescription = $"{pgm}/ORDJOBD", LimitCapabilities = "*YES",
                SpecialAuthorities = env.Tier == EnvironmentTier.Dev ? ["*JOBCTL"] : [],
                Status = env.Tier == EnvironmentTier.Stage ? "*DISABLED" : "*ENABLED",
            },
        };
        if (env.Tier == EnvironmentTier.Dev)
            list.Add(new() { Name = "SVCSHPDEV", Text = "Shipping label service account", NoPassword = true, GroupProfile = "ORDUSERS", JobDescription = $"{pgm}/ORDJOBD", LimitCapabilities = "*YES" });
        return list;
    }

    /// <summary>Everything that is not a table or a program: authorities, data areas, queues, job descriptions, messages, DDS, aliases.</summary>
    private static void Extras(TurnoverEnvironment env, int lv, Dictionary<string, SchemaSnapshot> result, string? cus, string? ord, string? pgm, string? src,
        TableDef header, TableDef line, TableDef customer)
    {
        var sfx = Sfx(env);
        header.Columns.First(c => c.Name == "TOTAL").Text = lv >= 3 ? "Order total incl. tax" : "Order total";
        header.Columns.First(c => c.Name == "TOTAL").Heading = "Order total";
        line.Checks.Add(new CheckConstraintDef("ORDLIN_QTY_CK", "QTY > 0"));
        if (lv >= 3) line.Checks.Add(new CheckConstraintDef("ORDLIN_PRICE_CK", "PRICE >= 0"));

        if (ord is not null && result.TryGetValue(ord, out var o))
        {
            o.Aliases.Add(new AliasDef { Name = "ORDHDR_A", BaseLibrary = ord, BaseTable = "ORDER_HEADER" });
            if (lv >= 3) o.Aliases.Add(new AliasDef { Name = "ORDHIST_PRD", BaseLibrary = "ORDPRD", BaseTable = "ORDER_HISTORY" });
            if (env.Tier == EnvironmentTier.QA) o.ForeignKeys.First(f => f.Name == "ORDLIN_ORDHDR_FK").CheckPending = true;

            void Aut(string obj, string type, string user, string aut) => o.Authorities.Add(new ObjectAuthorityDef { Object = obj, ObjectType = type, User = user, Authority = aut });
            foreach (var t in new[] { "ORDHDR", "ORDLIN" })
            {
                Aut(t, "*FILE", "*OWNER", "ORDOWNER");
                Aut(t, "*FILE", "*PUBLIC", lv >= 3 && t == "ORDHDR" ? "*CHANGE" : "*EXCLUDE");
                Aut(t, "*FILE", "ORDUSERS", "*CHANGE");
                Aut(t, "*FILE", "SVCORD" + sfx, "*ALL");
            }
            if (lv >= 3)
            {
                Aut("ORDAUD", "*FILE", "*PUBLIC", "*EXCLUDE");
                Aut("ORDAUD", "*FILE", "SVCORDPRD", "*CHANGE"); // copied from a production script by mistake
            }
        }

        if (pgm is not null && result.TryGetValue(pgm, out var g))
        {
            g.DataAreas.Add(new DataAreaDef { Name = "ORDCTL", Type = "*CHAR", Length = 50, Text = "Order control", Value = $"LIB={ord};MODE={(env.Tier == EnvironmentTier.Prod ? "LIVE" : "TEST")}" });
            g.DataAreas.Add(new DataAreaDef { Name = "ORDFTPPWD", Type = "*CHAR", Length = 32, Text = "FTP credentials", Value = $"pwd=Sx{sfx.ToLowerInvariant()}91!" });
            g.DataAreas.Add(new DataAreaDef { Name = "ORDLIMIT", Type = "*DEC", Length = lv >= 3 ? 11 : 9, Decimals = 2, Text = "Credit limit", Value = "5000.00" });
            g.DataQueues.Add(new DataQueueDef { Name = "ORDQ", MaxLength = lv >= 3 ? 256 : 128, Sequence = "*FIFO", Text = "New orders", CurrentMessages = env.Tier == EnvironmentTier.QA ? 3 : 0 });
            g.JobDescriptions.Add(new JobDescriptionDef
            {
                Name = "ORDJOBD", User = "SVCORD" + sfx, JobQueue = "QGPL/QBATCH", Text = "Order batch jobs",
                LibraryList = lv >= 3 ? ["SMAMIDI", pgm, ord ?? "", cus ?? "", "QGPL"] : [pgm, ord ?? "", cus ?? "", "QGPL"],
            });
            g.Messages.Add(new MessageDef { File = "ORDMSGF", Id = "ORD0001", Severity = 30, Text = lv >= 3 ? "Order &1 was not found." : "Order &1 not found." });
            if (lv >= 3) g.Messages.Add(new MessageDef { File = "ORDMSGF", Id = "ORD0002", Severity = 30, Text = "Customer &1 is not active.", SecondLevel = "&N Cause . . . : The customer status is not A." });

            // How each object was built: replayed on rebuild. Dev's ORDENTR now adopts owner authority (USRPRF *OWNER).
            foreach (var po in g.Programs)
                po.Options = po.Name switch
                {
                    "ORDENTR" => new CompileOptions { ActivationGroup = "ORDERS", TargetRelease = "V7R4M0", UserProfile = lv >= 3 ? "*OWNER" : "*USER", OptimizationLevel = 20, DebugData = true },
                    "ORDTOTR" => new CompileOptions { ActivationGroup = "*CALLER", TargetRelease = "V7R4M0", UserProfile = "*USER", SqlCommitment = "*NONE", SqlNaming = "*SQL", SqlDefaultSchema = ord },
                    "ORDUTIL" => new CompileOptions { ActivationGroup = "*CALLER", TargetRelease = "V7R4M0", UserProfile = "*USER", ExportSourceLibrary = src, ExportSourceFile = "QSRVSRC", ExportSourceMember = "ORDUTIL" },
                    "ORDRPT" => new CompileOptions { ActivationGroup = "*DFTACTGRP", TargetRelease = "V7R4M0", UserProfile = "*USER" },
                    _ => new CompileOptions { ActivationGroup = "*NEW", TargetRelease = "V7R4M0", UserProfile = "*USER" },
                };
            var srcLib = src ?? pgm;
            ProgramObject Dds(string name, string attr, string hash, DateTimeOffset changed, string text) => new()
            {
                Name = name, ObjectType = "*FILE", Attribute = attr, SourceLibrary = srcLib, SourceFile = "QDDSSRC", SourceMember = name,
                SourceHash = hash, SourceChanged = changed, Created = changed.AddMinutes(2), Text = text,
            };
            g.Programs.Add(Dds("ORDENTD", "DSPF", lv >= 3 ? "d2-shipmethod" : "d1", lv >= 3 ? Base.AddDays(200) : Base, "Order entry screens"));
            g.Programs.Add(Dds("ORDRPTP", "PRTF", "p1", Base, "Order report layout"));
            var entr = g.Programs.First(x => x.Name == "ORDENTR");
            entr.FileReferences.Add(new FileReference("*LIBL", "ORDENTD", ""));
            g.Programs.First(x => x.Name == "ORDRPT").FileReferences.Add(new FileReference("*LIBL", "ORDRPTP", ""));
            if (lv >= 3)
                g.Programs.Add(new ProgramObject
                {
                    Name = "SHPLBL", ObjectType = "*CMD", Attribute = "CMD", SourceLibrary = srcLib, SourceFile = "QCMDSRC", SourceMember = "SHPLBL",
                    SourceHash = "c1", SourceChanged = Base.AddDays(204), Created = Base.AddDays(204).AddMinutes(1), Text = "Print shipping label",
                    CommandProgram = $"{pgm}/SHPLBLR",
                });
            // UIM help / menu and the binding directory the ILE programs are compiled with.
            ProgramObject Ui(string name, string type, string attr, string file, string hash, DateTimeOffset changed, string text) => new()
            {
                Name = name, ObjectType = type, Attribute = attr, SourceLibrary = srcLib, SourceFile = file, SourceMember = name,
                SourceHash = hash, SourceChanged = changed, Created = changed.AddMinutes(1), Text = text,
            };
            g.Programs.Add(Ui("ORDHLP", "*PNLGRP", "PNLGRP", "QPNLSRC", lv >= 3 ? "h2-shipmethod" : "h1", lv >= 3 ? Base.AddDays(206) : Base, "Order entry help"));
            g.Programs.Add(Ui("ORDMNU", "*MENU", "UIM", "QMNUSRC", "m1", Base, "Order menu"));
            var bnd = new BindingDirectoryDef { Name = "ORDBND", Text = "Order application binding directory" };
            bnd.Entries.Add(new BindingEntryDef { Library = pgm, Object = "ORDUTIL", Type = "*SRVPGM", Activation = "*IMMED" });
            if (lv >= 3) bnd.Entries.Add(new BindingEntryDef { Library = "*LIBL", Object = "ORDTAX", Type = "*MODULE" });
            g.BindingDirectories.Add(bnd);

            foreach (var obj in new[] { ("ORDENTR", "*PGM"), ("ORDCTL", "*DTAARA"), ("ORDQ", "*DTAQ"), ("ORDENTD", "*FILE") })
            {
                g.Authorities.Add(new ObjectAuthorityDef { Object = obj.Item1, ObjectType = obj.Item2, User = "*PUBLIC", Authority = "*EXCLUDE" });
                g.Authorities.Add(new ObjectAuthorityDef { Object = obj.Item1, ObjectType = obj.Item2, User = "ORDUSERS", Authority = "*USE" });
            }
        }

        // A developer's personal library on the Dev partition: copies of only the objects being worked on.
        if (env.Tier == EnvironmentTier.Dev && ord is not null && result.TryGetValue(ord, out var dev))
        {
            var mine = new SchemaSnapshot { EnvironmentKey = env.Key, Library = "SMAMIDI", Logical = "SMAMIDI" };
            var hdr = System.Text.Json.JsonSerializer.Deserialize<TableDef>(System.Text.Json.JsonSerializer.Serialize(header))!;
            hdr.Columns.Add(new ColumnDef { Name = "NOTE", SystemName = "NOTE", DataType = "VARCHAR", Length = 200, Ordinal = 9, Text = "Free-text note" });
            hdr.FormatLevelId = hdr.ComputeFormatLevel();
            hdr.RowCount = 25;
            mine.Tables.Add(hdr);
            var sp = dev.Routines.First(r => r.Name == "SP_CREATE_ORDER");
            mine.Routines.Add(new RoutineDef
            {
                Name = sp.Name, SpecificName = sp.SpecificName, Parameters = sp.Parameters,
                Definition = sp.Definition.Replace(ord + ".", "SMAMIDI.").Replace("-- legacy history feed (copied from the old batch job)\n", "")
                    .Replace("'OP', 'GND');", "'OP', 'GND');\n  -- note captured at order entry"),
            });
            var entr = result[pgm!].Programs.First(x => x.Name == "ORDENTR");
            mine.Programs.Add(new ProgramObject
            {
                Name = "ORDENTR", ObjectType = "*PGM", Attribute = "RPGLE", SourceLibrary = "SMAMIDI", SourceFile = "QRPGLESRC", SourceMember = "ORDENTR",
                SourceHash = "e3-notes", SourceChanged = Base.AddDays(215), Created = Base.AddDays(215).AddMinutes(4), Text = "Order entry (notes)",
                BoundServicePrograms = entr.BoundServicePrograms.ToList(), FileReferences = entr.FileReferences.ToList(),
            });
            result["SMAMIDI"] = mine;
        }
    }
}
