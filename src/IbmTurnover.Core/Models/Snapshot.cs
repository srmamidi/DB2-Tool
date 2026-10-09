using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IbmTurnover.Core.Models;

/// <summary>
/// Marks data that changes with normal use (row counts, key maxima, generator positions, data-area values).
/// Excluded from fingerprints, so "the target changed since approval" means its STRUCTURE changed.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class VolatileAttribute : Attribute;

internal static class Fingerprints
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    foreach (var prop in ti.Properties)
                        if (prop.AttributeProvider?.IsDefined(typeof(VolatileAttribute), true) == true)
                            prop.ShouldSerialize = static (_, _) => false;
                },
            },
        },
    };

    public static string Of(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Options))))[..16];
}

/// <summary>Everything the tool knows about one library in one environment at one moment.</summary>
public sealed class SchemaSnapshot
{
    public string EnvironmentKey { get; set; } = "";
    public string Library { get; set; } = "";
    public string Logical { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TableDef> Tables { get; set; } = new();
    public List<ViewDef> Views { get; set; } = new();
    public List<IndexDef> Indexes { get; set; } = new();
    public List<ForeignKeyDef> ForeignKeys { get; set; } = new();
    public List<SequenceDef> Sequences { get; set; } = new();
    public List<RoutineDef> Routines { get; set; } = new();
    public List<TriggerDef> Triggers { get; set; } = new();
    public List<ProgramObject> Programs { get; set; } = new();
    public List<AliasDef> Aliases { get; set; } = new();
    public List<DataAreaDef> DataAreas { get; set; } = new();
    public List<DataQueueDef> DataQueues { get; set; } = new();
    public List<JobDescriptionDef> JobDescriptions { get; set; } = new();
    public List<MessageDef> Messages { get; set; } = new();
    /// <summary>Private and *PUBLIC authorities on objects in this library, plus each object's owner.</summary>
    public List<ObjectAuthorityDef> Authorities { get; set; } = new();
    public List<BindingDirectoryDef> BindingDirectories { get; set; } = new();

    public TableDef? Table(string name) => Tables.FirstOrDefault(t => Eq(t.Name, name) || Eq(t.SystemName, name));

    /// <summary>Stable hash of the definitions (not capture time, not volatile data). Used to detect "target changed after approval".</summary>
    /// <remarks>Lists are sorted first: catalog views return rows in no guaranteed order, and a different query plan must
    /// not look like "the target changed since approval".</remarks>
    public string Fingerprint() => Fingerprints.Of(new
    {
        Library,
        Tables = Tables.OrderBy(x => x.Name, C).ToList(), Views = Views.OrderBy(x => x.Name, C).ToList(),
        Indexes = Indexes.OrderBy(x => x.Name, C).ToList(), ForeignKeys = ForeignKeys.OrderBy(x => x.Name, C).ToList(),
        Sequences = Sequences.OrderBy(x => x.Name, C).ToList(), Routines = Routines.OrderBy(x => x.SpecificName, C).ToList(),
        Triggers = Triggers.OrderBy(x => x.Name, C).ToList(), Programs = Programs.OrderBy(x => x.Name, C).ThenBy(x => x.ObjectType, C).ToList(),
        Aliases = Aliases.OrderBy(x => x.Name, C).ToList(), DataAreas = DataAreas.OrderBy(x => x.Name, C).ToList(),
        DataQueues = DataQueues.OrderBy(x => x.Name, C).ToList(), JobDescriptions = JobDescriptions.OrderBy(x => x.Name, C).ToList(),
        Messages = Messages.OrderBy(x => x.File, C).ThenBy(x => x.Id, C).ToList(),
        Authorities = Authorities.OrderBy(x => x.Object, C).ThenBy(x => x.ObjectType, C).ThenBy(x => x.User, C).ToList(),
        BindingDirectories = BindingDirectories.OrderBy(x => x.Name, C).ToList(),
    });

    private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

    internal static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public sealed class TableDef
{
    public string Name { get; set; } = "";
    /// <summary>10-character IBM i object name. RPG/CL programs open the file by this name.</summary>
    public string SystemName { get; set; } = "";
    public string Text { get; set; } = "";
    public List<ColumnDef> Columns { get; set; } = new();
    public List<string> PrimaryKey { get; set; } = new();
    public string? PrimaryKeyName { get; set; }
    public List<List<string>> UniqueKeys { get; set; } = new();
    /// <summary>Record format level identifier. RPG programs compiled against an older id fail with CPF4131.</summary>
    public string FormatLevelId { get; set; } = "";
    [Volatile] public long RowCount { get; set; }
    /// <summary>DDS-described physical file (not created with SQL). ALTER works but leaves the DDS source behind.</summary>
    public bool IsDds { get; set; }
    public List<CheckConstraintDef> Checks { get; set; } = new();
    public bool Journaled { get; set; }
    public string? JournalName { get; set; }

    public ColumnDef? Column(string name) =>
        Columns.FirstOrDefault(c => SchemaSnapshot.Eq(c.Name, name) || SchemaSnapshot.Eq(c.SystemName, name));

    /// <summary>Same algorithm for every env so ids line up in the sample data. Live mode reads the real id.</summary>
    public string ComputeFormatLevel()
    {
        var sig = string.Join("|", Columns.OrderBy(c => c.Ordinal).Select(c => $"{c.SystemName}:{c.TypeSignature}:{c.Nullable}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sig)))[..13];
    }
}

public sealed class ColumnDef
{
    public string Name { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string DataType { get; set; } = "";
    public int? Length { get; set; }
    public int? Scale { get; set; }
    public bool Nullable { get; set; } = true;
    public string? Default { get; set; }
    public int? Ccsid { get; set; }
    public int Ordinal { get; set; }
    public bool IsIdentity { get; set; }
    /// <summary>Next value the identity generator will hand out in THIS environment.</summary>
    [Volatile] public long? IdentityNextValue { get; set; }
    /// <summary>Observed MAX(column) in this environment (key columns only).</summary>
    [Volatile] public long? MaxValue { get; set; }
    /// <summary>LABEL ON COLUMN ... TEXT IS (50 chars).</summary>
    public string? Text { get; set; }
    /// <summary>LABEL ON COLUMN ... IS (column heading, 60 chars).</summary>
    public string? Heading { get; set; }

    public string TypeSignature => TypeText(DataType, Length, Scale);

    public static string TypeText(string type, int? length, int? scale)
    {
        var t = type.ToUpperInvariant();
        if (t == "TIMESTAMP") return length is null or 6 ? "TIMESTAMP" : $"TIMESTAMP({length})";
        if (t is "INTEGER" or "INT" or "BIGINT" or "SMALLINT" or "DATE" or "TIME" or "DOUBLE" or "REAL" or "XML" or "ROWID")
            return t == "INT" ? "INTEGER" : t;
        if (length is null) return t;
        return scale is null || t is not ("DECIMAL" or "NUMERIC" or "DEC") ? $"{t}({length})" : $"{t}({length},{scale})";
    }
}

public sealed class IndexDef
{
    public string Name { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string Table { get; set; } = "";
    public List<IndexColumn> Columns { get; set; } = new();
    public bool IsUnique { get; set; }
    /// <summary>Db2 for i UNIQUE WHERE NOT NULL: NULLs are not compared. A plain UNIQUE index treats NULLs as equal.</summary>
    public bool UniqueWhereNotNull { get; set; }

    public string Signature =>
        $"{Table.ToUpperInvariant()}|{IsUnique}{(UniqueWhereNotNull ? "-WNN" : "")}|{string.Join(",", Columns.Select(c => c.Name.ToUpperInvariant() + (c.Descending ? " DESC" : "")))}";
}

public sealed record IndexColumn(string Name, bool Descending = false);

public sealed class ForeignKeyDef
{
    public string Name { get; set; } = "";
    public string Table { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public string RefLibrary { get; set; } = "";
    public string RefTable { get; set; } = "";
    public List<string> RefColumns { get; set; } = new();
    public string DeleteRule { get; set; } = "RESTRICT";
    public string UpdateRule { get; set; } = "RESTRICT";
    /// <summary>Db2 for i marks a constraint CHECK PENDING when existing rows violate it (after CPYF, RSTOBJ, ...).</summary>
    public bool CheckPending { get; set; }
}

public sealed class SequenceDef
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "BIGINT";
    public long Start { get; set; } = 1;
    public long Increment { get; set; } = 1;
    public long? MinValue { get; set; }
    public long? MaxValue { get; set; }
    public bool Cycle { get; set; }
    public int Cache { get; set; } = 20;
    /// <summary>Next value NEXT VALUE FOR will return in this environment.</summary>
    [Volatile] public long? NextValue { get; set; }
    public string? BoundTable { get; set; }
    public string? BoundColumn { get; set; }
}

public sealed class RoutineDef
{
    public string Name { get; set; } = "";
    public string SpecificName { get; set; } = "";
    /// <summary>PROCEDURE or FUNCTION.</summary>
    public string Kind { get; set; } = "PROCEDURE";
    /// <summary>SQL, RPGLE, CL, ...</summary>
    public string Language { get; set; } = "SQL";
    public List<string> Parameters { get; set; } = new();
    /// <summary>For external routines: 'LIB/PGM' or 'LIB/SRVPGM(PROC)'.</summary>
    public string? ExternalName { get; set; }
    /// <summary>Full CREATE OR REPLACE statement (from QSYS2.GENERATE_SQL in live mode).</summary>
    public string Definition { get; set; } = "";
}

public sealed class ViewDef
{
    public string Name { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string Definition { get; set; } = "";
    /// <summary>LIB.TABLE names this view reads.</summary>
    public List<string> References { get; set; } = new();
}

public sealed class TriggerDef
{
    public string Name { get; set; } = "";
    public string Table { get; set; } = "";
    public string Definition { get; set; } = "";
}

public sealed class ProgramObject
{
    /// <summary>SourceHash when the recorded source member does not exist on the partition.</summary>
    public const string MissingSource = "*MISSING";
    public string Name { get; set; } = "";
    /// <summary>*PGM, *SRVPGM, *FILE (DDS), *CMD, *MENU or *PNLGRP.</summary>
    public string ObjectType { get; set; } = "*PGM";
    /// <summary>RPGLE, SQLRPGLE, CLLE.</summary>
    public string Attribute { get; set; } = "RPGLE";
    public string SourceLibrary { get; set; } = "";
    public string SourceFile { get; set; } = "QRPGLESRC";
    public string SourceMember { get; set; } = "";
    public string SourceHash { get; set; } = "";
    /// <summary>UTC. Partitions may run in different time zones; the reader converts with CURRENT TIMEZONE.</summary>
    /// <remarks>Volatile: converted with the partition's current UTC offset, so it shifts an hour at a DST change, and a
    /// rebuild on a replica gets its own time. The source hash says whether the source changed.</remarks>
    [Volatile] public DateTimeOffset SourceChanged { get; set; }
    /// <summary>UTC.</summary>
    [Volatile] public DateTimeOffset Created { get; set; }
    /// <summary>For *CMD: the command processing program ('LIB/PGM' or '*LIBL/PGM').</summary>
    public string? CommandProgram { get; set; }
    /// <summary>Modules bound into this program. More than one cannot be rebuilt by a single compile command.</summary>
    public int ModuleCount { get; set; } = 1;
    /// <summary>How the object was built (QSYS2.PROGRAM_INFO / BOUND_MODULE_INFO). Null when not known; defaults are used then.</summary>
    public CompileOptions? Options { get; set; }
    public string Text { get; set; } = "";
    /// <summary>'LIB/SRVPGM' for each bound service program.</summary>
    public List<string> BoundServicePrograms { get; set; } = new();
    public List<FileReference> FileReferences { get; set; } = new();
}

/// <summary>A file a program opens. Library *LIBL means "resolved through the job library list".</summary>
public sealed record FileReference(string Library, string File, string FormatLevelId);

public sealed record CheckConstraintDef(string Name, string Clause);

public sealed class AliasDef
{
    public string Name { get; set; } = "";
    public string BaseLibrary { get; set; } = "";
    public string BaseTable { get; set; } = "";
    public string? Member { get; set; }
}

public sealed class DataAreaDef
{
    public string Name { get; set; } = "";
    /// <summary>*CHAR, *DEC or *LGL.</summary>
    public string Type { get; set; } = "*CHAR";
    public int Length { get; set; }
    public int Decimals { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Configuration held in the data area. Environment-specific, so it is reported but not promoted by default.</summary>
    [Volatile] public string? Value { get; set; }
}

public sealed class DataQueueDef
{
    public string Name { get; set; } = "";
    public int MaxLength { get; set; }
    /// <summary>*FIFO, *LIFO or *KEYED.</summary>
    public string Sequence { get; set; } = "*FIFO";
    public int KeyLength { get; set; }
    public bool IncludeSenderId { get; set; }
    public string Text { get; set; } = "";
    [Volatile] public long CurrentMessages { get; set; }
}

public sealed class JobDescriptionDef
{
    public string Name { get; set; } = "";
    /// <summary>Initial library list, in order.</summary>
    public List<string> LibraryList { get; set; } = new();
    /// <summary>USER parameter (*RQD or a profile).</summary>
    public string User { get; set; } = "*RQD";
    public string JobQueue { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>One message description in a message file (*MSGF).</summary>
public sealed class MessageDef
{
    public string File { get; set; } = "";
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string? SecondLevel { get; set; }
    public int Severity { get; set; }
}

/// <summary>One authority on one object. User *PUBLIC is the public authority; User *OWNER records the owner in Authority.</summary>
public sealed class ObjectAuthorityDef
{
    public string Object { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>*ALL, *CHANGE, *USE, *EXCLUDE or USER DEFINED (for *OWNER: the owning profile).</summary>
    public string Authority { get; set; } = "";
}

/// <summary>An IBM i user profile, including service accounts. Passwords are never read.</summary>
public sealed class UserProfileDef
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "*ENABLED";
    public string UserClass { get; set; } = "*USER";
    public List<string> SpecialAuthorities { get; set; } = new();
    public string GroupProfile { get; set; } = "*NONE";
    public List<string> SupplementalGroups { get; set; } = new();
    public string CurrentLibrary { get; set; } = "*CRTDFT";
    public string InitialProgram { get; set; } = "*NONE";
    public string InitialMenu { get; set; } = "MAIN";
    public string LimitCapabilities { get; set; } = "*NO";
    public string JobDescription { get; set; } = "QGPL/QDFTJOBD";
    public bool NoPassword { get; set; }
    public string Text { get; set; } = "";

    public bool IsServiceAccount => NoPassword || Text.Contains("service", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A whole environment at one moment: every mapped library plus the tracked user profiles.</summary>
public sealed class EnvironmentSnapshot
{
    public string EnvironmentKey { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, SchemaSnapshot> Libraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UserProfileDef> Profiles { get; set; } = new();
    /// <summary>Authorization lists, job schedule entries, system values and IFS files (null in snapshots taken before they were read).</summary>
    public SystemObjects? System { get; set; }

    public string Fingerprint() => Fingerprints.Of(new
    {
        Libraries = Libraries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => k.Value.Fingerprint()).ToList(),
        Profiles = Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        System = (System ?? new SystemObjects()).Fingerprint(),
    });
}

/// <summary>
/// The compile and bind options an object was created with, replayed when it is rebuilt so the new object behaves
/// like the one that was tested (activation group, adopted authority, SQL isolation ...).
/// </summary>
public sealed class CompileOptions
{
    /// <summary>*DFTACTGRP, *NEW, *CALLER or a name.</summary>
    public string? ActivationGroup { get; set; }
    /// <summary>VxRxMx.</summary>
    public string? TargetRelease { get; set; }
    /// <summary>*USER or *OWNER (adopts the owner's authority).</summary>
    public string? UserProfile { get; set; }
    /// <summary>*YES / *NO.</summary>
    public string? UseAdoptedAuthority { get; set; }
    /// <summary>10, 20, 30 or 40.</summary>
    public int? OptimizationLevel { get; set; }
    public bool? DebugData { get; set; }
    public string? SqlCommitment { get; set; }
    public string? SqlNaming { get; set; }
    public string? SqlDateFormat { get; set; }
    public string? SqlDefaultSchema { get; set; }
    public string? SqlDynamicUserProfile { get; set; }
    /// <summary>Service programs: where the binder source (export list) is. LIB/FILE(MBR).</summary>
    public string? ExportSourceLibrary { get; set; }
    public string? ExportSourceFile { get; set; }
    public string? ExportSourceMember { get; set; }

    public string Describe() => string.Join(" ", new[]
    {
        ActivationGroup is null ? null : $"ACTGRP({ActivationGroup})", TargetRelease is null ? null : $"TGTRLS({TargetRelease})",
        UserProfile is null ? null : $"USRPRF({UserProfile})", UseAdoptedAuthority is null ? null : $"USEADPAUT({UseAdoptedAuthority})",
        OptimizationLevel is null ? null : $"OPTIMIZE({OptimizationLevel})", DebugData is null ? null : $"DEBUG({(DebugData.Value ? "*YES" : "*NO")})",
        SqlCommitment is null ? null : $"COMMIT({SqlCommitment})", SqlNaming is null ? null : $"NAMING({SqlNaming})",
        SqlDateFormat is null ? null : $"DATFMT({SqlDateFormat})", SqlDefaultSchema is null ? null : $"DFTRDBCOL({SqlDefaultSchema})",
        SqlDynamicUserProfile is null ? null : $"DYNUSRPRF({SqlDynamicUserProfile})",
        ExportSourceFile is null ? null : $"EXPORT({ExportSourceLibrary}/{ExportSourceFile}({ExportSourceMember}))",
    }.Where(x => x is not null));
}
