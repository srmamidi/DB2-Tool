using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Compare;

/// <summary>
/// Rebuild commands that reproduce how an object was originally created. When the catalog recorded the options
/// (<see cref="ProgramObject.Options"/>), they are replayed: activation group, target release, USRPRF (adopted
/// authority), optimization, debug data, SQL precompile options, the exact list of bound service programs, and the
/// service program's binder source. ILE objects in a named / *NEW / *CALLER activation group are built in two steps
/// (create module, then CRTPGM / CRTSRVPGM) because only the bind step accepts the bound service program list.
/// </summary>
public static class Builds
{
    /// <param name="mapObject">Maps 'LIB/OBJ' from the build's source environment to the target.</param>
    /// <param name="mapLibrary">Maps a library name (SQL default schema, binder source library) the same way.</param>
    /// <returns>CL commands in order, or null when the tool cannot build this object (manual build).</returns>
    public static IReadOnlyList<string>? Commands(ProgramObject p, string pgmLib, string srcLib, bool exists,
        Func<string, string> mapObject, Func<string, string> mapLibrary, string? commandProgram = null)
    {
        if (p.ModuleCount > 1 || string.IsNullOrEmpty(p.SourceFile)) return null;
        var type = p.ObjectType.ToUpperInvariant();
        var attr = p.Attribute.ToUpperInvariant();
        if (type is "*FILE" or "*CMD" or "*MENU" or "*PNLGRP") return Split(Ddl.CompileCommand(p, pgmLib, srcLib, exists, commandProgram));

        var o = p.Options;
        if (o is null) return Split(Ddl.CompileCommand(p, pgmLib, srcLib, exists, commandProgram)); // not recorded: source (H-spec) decides

        var name = SqlText.ClName(p.Name);
        var lib = SqlText.ClName(pgmLib);
        var src = $"SRCFILE({SqlText.ClName(srcLib)}/{SqlText.ClName(p.SourceFile)}) SRCMBR({SqlText.ClName(string.IsNullOrEmpty(p.SourceMember) ? p.Name : p.SourceMember)})";
        string Opt(string kw, string? v) => string.IsNullOrWhiteSpace(v) ? "" : $" {kw}({v.Trim()})";
        var tgtrls = Opt("TGTRLS", o.TargetRelease);
        var usrprf = Opt("USRPRF", o.UserProfile);
        // Only the C compiler takes 40; RPG, CL and COBOL accept *NONE / *BASIC / *FULL, so 40 (set later with CHGPGM) maps to *FULL.
        var optimize = o.OptimizationLevel switch { 10 => " OPTIMIZE(*NONE)", 20 => " OPTIMIZE(*BASIC)", 30 => " OPTIMIZE(*FULL)", 40 => attr == "CLE" ? " OPTIMIZE(40)" : " OPTIMIZE(*FULL)", _ => "" };
        var dbg = o.DebugData switch { true => " DBGVIEW(*SOURCE)", false => " DBGVIEW(*NONE)", _ => "" };
        var sql = Opt("COMMIT", o.SqlCommitment ?? "*NONE") + Opt("NAMING", o.SqlNaming) + Opt("DATFMT", o.SqlDateFormat)
                  + Opt("DFTRDBCOL", o.SqlDefaultSchema is null or "*NONE" ? o.SqlDefaultSchema : mapLibrary(o.SqlDefaultSchema))
                  + Opt("DYNUSRPRF", o.SqlDynamicUserProfile);
        var bnd = p.BoundServicePrograms.Count == 0 ? ""
            : $" BNDSRVPGM({string.Join(" ", p.BoundServicePrograms.Select(b => "(" + Ddl.QualifiedCl(mapObject(b)) + ")"))})";
        var ag = o.ActivationGroup?.Trim();
        var dftActGrp = ag is null || ag.Equals("*DFTACTGRP", StringComparison.OrdinalIgnoreCase);
        var module = $"QTEMP/{name}";

        // Module step per language (used for two-step builds and service programs).
        string? Module() => attr switch
        {
            "RPGLE" => $"CRTRPGMOD MODULE({module}) {src}{optimize}{dbg}{tgtrls} REPLACE(*YES)",
            "SQLRPGLE" => $"CRTSQLRPGI OBJ({module}) {src} OBJTYPE(*MODULE){sql}{tgtrls}{dbg} REPLACE(*YES)",
            "CLLE" => $"CRTCLMOD MODULE({module}) {src}{optimize}{dbg}{tgtrls} REPLACE(*YES)",
            "CBLLE" => $"CRTCBLMOD MODULE({module}) {src}{optimize}{dbg}{tgtrls} REPLACE(*YES)",
            "SQLCBLLE" => $"CRTSQLCBLI OBJ({module}) {src} OBJTYPE(*MODULE){sql}{tgtrls}{dbg} REPLACE(*YES)",
            "CLE" => $"CRTCMOD MODULE({module}) {src}{optimize}{dbg}{tgtrls} REPLACE(*YES)",
            "SQLC" => $"CRTSQLCI OBJ({module}) {src} OBJTYPE(*MODULE){sql}{tgtrls}{dbg} REPLACE(*YES)",
            _ => null,
        };

        if (type == "*SRVPGM")
        {
            var mod = Module();
            if (mod is null) return null;
            var export = o.ExportSourceFile is { Length: > 0 } && o.ExportSourceLibrary is { Length: > 0 }
                ? $" EXPORT(*SRCFILE) SRCFILE({SqlText.ClName(mapLibrary(o.ExportSourceLibrary))}/{SqlText.ClName(o.ExportSourceFile)}) SRCMBR({SqlText.ClName(o.ExportSourceMember ?? p.Name)})"
                : " EXPORT(*ALL)";
            return [mod, $"CRTSRVPGM SRVPGM({lib}/{name}) MODULE({module}){export}{bnd} ACTGRP({(dftActGrp ? "*CALLER" : ag)}){tgtrls}{usrprf} REPLACE(*YES)"];
        }
        if (type != "*PGM") return null;

        switch (attr)
        {
            case "CLP": return [$"CRTCLPGM PGM({lib}/{name}) {src}{usrprf}{tgtrls} REPLACE(*YES)"];
            case "RPG" or "RPG38": return [$"CRTRPGPGM PGM({lib}/{name}) {src}{usrprf}{tgtrls} REPLACE(*YES)"];
        }
        if (dftActGrp)
            return attr switch
            {
                "RPGLE" => [$"CRTBNDRPG PGM({lib}/{name}) {src} DFTACTGRP(*YES){optimize}{dbg}{tgtrls}{usrprf} REPLACE(*YES)"],
                "CLLE" => [$"CRTBNDCL PGM({lib}/{name}) {src} DFTACTGRP(*YES){optimize}{dbg}{tgtrls}{usrprf} REPLACE(*YES)"],
                "SQLRPGLE" => [$"CRTSQLRPGI OBJ({lib}/{name}) {src} OBJTYPE(*PGM){sql}{tgtrls}{dbg}{usrprf} COMPILEOPT('DFTACTGRP(*YES)') REPLACE(*YES)"],
                _ => null,   // COBOL / C have no default activation group
            };
        var step1 = Module();
        return step1 is null ? null : [step1, $"CRTPGM PGM({lib}/{name}) MODULE({module}){bnd} ACTGRP({ag}){tgtrls}{usrprf} REPLACE(*YES)"];
    }

    private static IReadOnlyList<string>? Split(string? commands) => commands?.Split('\n');
}
