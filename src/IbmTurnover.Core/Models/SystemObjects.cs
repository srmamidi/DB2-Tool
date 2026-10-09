namespace IbmTurnover.Core.Models;

/// <summary>
/// Objects that belong to the partition rather than to a library: authorization lists, job scheduler entries,
/// system values and IFS stream files. Only the names / paths listed in Turnover settings are read.
/// </summary>
public sealed class SystemObjects
{
    public List<AuthorizationListDef> AuthorizationLists { get; set; } = new();
    public List<JobScheduleDef> JobSchedules { get; set; } = new();
    public List<SystemValueDef> SystemValues { get; set; } = new();
    public List<IfsFileDef> IfsFiles { get; set; } = new();

    public string Fingerprint() => Fingerprints.Of(new
    {
        A = AuthorizationLists.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        J = JobSchedules.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        S = SystemValues.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        F = IfsFiles.OrderBy(x => x.Logical, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RelativePath, StringComparer.Ordinal).ToList(),
    });
}

public sealed class AuthorizationListDef
{
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";
    /// <summary>Users on the list (including *PUBLIC) and their authority. The owner's own *ALL entry is not listed.</summary>
    public List<AuthorizationListEntry> Entries { get; set; } = new();
}

public sealed class AuthorizationListEntry
{
    public string User { get; set; } = "";
    public string Authority { get; set; } = "";
}

/// <summary>One WRKJOBSCDE entry (QSYS2.SCHEDULED_JOB_INFO).</summary>
public sealed class JobScheduleDef
{
    public string Name { get; set; } = "";
    /// <summary>Assigned by the system; differs per partition, so it is not part of the comparison.</summary>
    [Volatile] public int EntryNumber { get; set; }
    /// <summary>SCHEDULED, HELD or SAVED.</summary>
    public string Status { get; set; } = "SCHEDULED";
    /// <summary>*ONCE, *WEEKLY or *MONTHLY.</summary>
    public string Frequency { get; set; } = "*WEEKLY";
    /// <summary>*NONE, *CURRENT, *MONTHSTR, *MONTHEND, a weekday or a date (yyyy-MM-dd).</summary>
    public string ScheduledDate { get; set; } = "*NONE";
    /// <summary>"*MON *WED", "*ALL" or "*NONE" (space separated, the ADDJOBSCDE SCDDAY form).</summary>
    public string ScheduledDays { get; set; } = "*NONE";
    /// <summary>HH:mm:ss.</summary>
    public string ScheduledTime { get; set; } = "00:00:00";
    /// <summary>"1 3" (first and third) or "" when not used.</summary>
    public string RelativeDays { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>LIB/JOBD.</summary>
    public string JobDescription { get; set; } = "*USRPRF";
    /// <summary>LIB/JOBQ or *JOBD.</summary>
    public string JobQueue { get; set; } = "*JOBD";
    public string User { get; set; } = "*CURRENT";
    public string Text { get; set; } = "";
}

/// <summary>One system value (QSYS2.SYSTEM_VALUE_INFO).</summary>
public sealed class SystemValueDef
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Numeric system values are set with VALUE(n); character ones with VALUE('text').</summary>
    public bool Numeric { get; set; }
}

/// <summary>A stream file under one of an environment's IfsPaths roots.</summary>
public sealed class IfsFileDef
{
    /// <summary>Logical IFS location (the key in IfsPaths).</summary>
    public string Logical { get; set; } = "";
    /// <summary>Path below the location's root, e.g. "conf/app.json" (forward slashes, no leading slash).</summary>
    public string RelativePath { get; set; } = "";
    /// <summary>Full path in this environment.</summary>
    [Volatile] public string Path { get; set; } = "";
    public long Size { get; set; }
    /// <summary>SHA-256 of the content (first 16 hex). Null when the file is larger than MaxIfsCompareBytes.</summary>
    public string? Hash { get; set; }
    public int Ccsid { get; set; }
    /// <summary>UTC. Differs between environments even for identical files, so it is never compared.</summary>
    [Volatile] public DateTimeOffset Changed { get; set; }
}

/// <summary>A binding directory and its entries (QSYS2.BINDING_DIRECTORY_INFO).</summary>
public sealed class BindingDirectoryDef
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public List<BindingEntryDef> Entries { get; set; } = new();
}

public sealed class BindingEntryDef
{
    /// <summary>Library or *LIBL.</summary>
    public string Library { get; set; } = "*LIBL";
    public string Object { get; set; } = "";
    /// <summary>*MODULE or *SRVPGM.</summary>
    public string Type { get; set; } = "*SRVPGM";
    /// <summary>*IMMED or *DEFER (service programs only).</summary>
    public string Activation { get; set; } = "*IMMED";

    public string Key => $"{Object}|{Type}".ToUpperInvariant();
    public override string ToString() => $"{Library}/{Object} {Type}{(Type.Equals("*SRVPGM", StringComparison.OrdinalIgnoreCase) ? " " + Activation : "")}";
}
