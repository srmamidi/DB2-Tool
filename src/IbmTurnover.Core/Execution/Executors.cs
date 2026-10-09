using IbmTurnover.Core.Compare;
using System.Data.Common;
using IbmTurnover.Core.Catalog;
using System.Diagnostics;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;

namespace IbmTurnover.Core.Execution;

public interface IDeploymentExecutor
{
    /// <summary>Runs steps in order and stops at the first failure. Never throws for a failed step: it reports it.</summary>
    Task<DeploymentRun> RunAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> steps, string mode, string user, CancellationToken ct = default);

    /// <summary>Undoes the given (completed) steps in reverse order.</summary>
    Task<DeploymentRun> RollbackAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> completed, string user, CancellationToken ct = default);
}

/// <summary>Walks the plan without connecting to anything. Used for dry runs, and for every run in Sample mode.</summary>
public sealed class SimulatedExecutor : IDeploymentExecutor
{
    public Task<DeploymentRun> RunAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> steps, string mode, string user, CancellationToken ct = default)
    {
        var run = new DeploymentRun { Mode = mode, User = user, StartedAt = DateTimeOffset.UtcNow };
        foreach (var s in steps)
        {
            ct.ThrowIfCancellationRequested();
            run.Results.Add(new StepResult
            {
                Order = s.Order, Title = s.Title, Status = StepStatus.Simulated,
                Message = s.Kind == StepKind.Check ? "Would run check; must return 0." : $"Would run on {target.Key}: {Shorten(DeploymentPlanner.AsSql(s))}",
            });
        }
        run.Succeeded = true;
        run.EndedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(run);
    }

    public Task<DeploymentRun> RollbackAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> completed, string user, CancellationToken ct = default)
    {
        var run = new DeploymentRun { Mode = "Rollback", User = user, StartedAt = DateTimeOffset.UtcNow };
        foreach (var s in completed.OrderByDescending(x => x.Order))
            run.Results.Add(new StepResult
            {
                Order = s.Order, Title = "Undo: " + s.Title,
                Status = s.Rollback is null || DeploymentPlanner.IsManual(s.Rollback) ? StepStatus.Skipped : StepStatus.Simulated,
                Message = s.Rollback is null ? "Nothing to undo (or restored by an earlier backup step)." : DeploymentPlanner.IsManual(s.Rollback) ? "Manual: " + s.Rollback.TrimStart('-', ' ') : "Would run: " + Shorten(s.Rollback),
            });
        run.Succeeded = true;
        run.EndedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(run);
    }

    internal static string Shorten(string s) => s.Length <= 220 ? s : s[..217] + "...";
}

/// <summary>
/// Executes against IBM i over ODBC on ONE connection (so QTEMP, the library list set by CHGLIBL and
/// the job's state carry across steps). Autocommit; DDL on Db2 for i is not rolled back by a transaction
/// unless every object is journaled, so recovery is by the explicit rollback statements.
/// </summary>
public sealed class Db2DeploymentExecutor(IDb2ConnectionFactory connections, TurnoverOptions options) : IDeploymentExecutor
{
    public async Task<DeploymentRun> RunAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> steps, string mode, string user, CancellationToken ct = default)
    {
        var run = new DeploymentRun { Mode = mode, User = user, StartedAt = DateTimeOffset.UtcNow, Succeeded = true };
        DbConnection con;
        try { con = await connections.OpenAsync(target, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing ran: say so per step instead of throwing away the run (a replica can be unreachable after the primary succeeded).
            run.Succeeded = false;
            foreach (var s in steps.OrderBy(x => x.Order))
                run.Results.Add(new StepResult { Order = s.Order, Title = s.Title, Status = s == steps.OrderBy(x => x.Order).First() ? StepStatus.Failed : StepStatus.Skipped,
                    Message = s == steps.OrderBy(x => x.Order).First() ? $"Could not connect to {target.Key}: {ex.Message}" : "Not run: no connection." });
            run.EndedAt = DateTimeOffset.UtcNow;
            return run;
        }
        await using var _ = con;
        foreach (var s in steps.OrderBy(x => x.Order))
        {
            // Finally-phase steps (release a held job queue, restart a subsystem) run even after a failure.
            if (!run.Succeeded && !s.AlwaysRun)
            {
                run.Results.Add(new StepResult { Order = s.Order, Title = s.Title, Status = StepStatus.Skipped, Message = "Not run: an earlier step failed." });
                continue;
            }
            if (s.RunIfSucceeded is int needed && run.Results.FirstOrDefault(r => r.Order == needed)?.Status != StepStatus.Succeeded)
            {
                run.Results.Add(new StepResult { Order = s.Order, Title = s.Title, Status = StepStatus.Skipped, Message = $"Not run: step {needed} (the command it undoes) did not run." });
                continue;
            }
            if (s.Kind is StepKind.CopySource or StepKind.CopyIfs)
            {
                var sw0 = Stopwatch.StartNew();
                var copy = new StepResult { Order = s.Order, Title = s.Title };
                try
                {
                    copy.Message = s.Kind == StepKind.CopySource
                        ? $"{await CopyMemberAsync(con, s, ct)} source line(s) copied."
                        : $"{await CopyStreamFileAsync(con, s, ct):N0} byte(s) copied.";
                    copy.Status = StepStatus.Succeeded;
                }
                catch (Exception ex)
                {
                    // Any failure is recorded on the step: an escaping exception would lose the run log and skip Finally steps.
                    copy.Status = StepStatus.Failed; copy.Message = ex.Message; run.Succeeded = false;
                }
                copy.DurationMs = sw0.ElapsedMilliseconds;
                run.Results.Add(copy);
                continue;
            }
            if (DeploymentPlanner.IsManual(s.Command))
            {
                run.Results.Add(new StepResult { Order = s.Order, Title = s.Title, Status = StepStatus.Skipped, Message = "Manual step: " + s.Command.TrimStart('-', ' ') });
                continue;
            }
            var sw = Stopwatch.StartNew();
            var result = new StepResult { Order = s.Order, Title = s.Title };
            try
            {
                await using var cmd = con.CreateCommand();
                cmd.CommandText = DeploymentPlanner.AsSql(s);
                cmd.CommandTimeout = 0;
                if (s.Kind == StepKind.Check)
                {
                    // A check that returns a non-number is a failed check, not a crash (SqlText.ToLong never throws).
                    var value = Compare.SqlText.ToLong(await cmd.ExecuteScalarAsync(ct)) ?? -1;
                    result.Status = value == 0 ? StepStatus.Succeeded : StepStatus.Failed;
                    result.Message = value == 0 ? "Check passed (0)." : $"Check returned {value:N0}; expected 0. Deployment stopped before any change was made by later steps.";
                }
                else
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                    result.Status = StepStatus.Succeeded;
                    result.Message = "OK";
                }
            }
            catch (Exception ex)
            {
                result.Status = StepStatus.Failed;
                result.Message = ex.Message;
            }
            result.DurationMs = sw.ElapsedMilliseconds;
            run.Results.Add(result);
            if (result.Status == StepStatus.Failed) run.Succeeded = false;
        }
        run.EndedAt = DateTimeOffset.UtcNow;
        return run;
    }

    /// <summary>
    /// Copies one source member from the source partition to the target partition: reads SRCSEQ/SRCDAT/SRCDTA through
    /// a QTEMP alias on the source connection, then replaces the target member's lines the same way. The target member
    /// is added first if it does not exist, with the source member's source type.
    /// </summary>
    private async Task<int> CopyMemberAsync(DbConnection target, PlanStep s, CancellationToken ct)
    {
        static (string Lib, string File, string Mbr) Parse(string? v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v ?? "", @"^([A-Z0-9_#@$]{1,10})/([A-Z0-9_#@$]{1,10})\(([A-Z0-9_#@$]{1,10})\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) throw new TurnoverException($"Malformed member reference '{v}'.");
            return (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant(), m.Groups[3].Value.ToUpperInvariant());
        }
        var from = Parse(s.CopyFrom);
        var to = Parse(s.CopyTo);
        var sourceEnv = options.Env(s.SourceEnvironment ?? throw new TurnoverException("Copy step without a source environment."));

        var lines = new List<object[]>();
        string? srcType;
        await using (var src = await connections.OpenAsync(sourceEnv, ct))
        {
            // No row = no member (Convert.ToString(null) would be "", which is also a legal blank source type).
            var type = await Scalar(src, "SELECT COALESCE(SOURCE_TYPE, '') FROM QSYS2.SYSPARTITIONSTAT WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? AND TABLE_PARTITION = ?", ct, from.Lib, from.File, from.Mbr);
            srcType = type is null or DBNull ? null : Convert.ToString(type, System.Globalization.CultureInfo.InvariantCulture)!.Trim();
            if (srcType is null) throw new TurnoverException($"Source member {s.CopyFrom} does not exist in {sourceEnv.Key}.");
            await Exec(src, $"CREATE OR REPLACE ALIAS QTEMP.TOVCPYSRC FOR {from.Lib}.{from.File} ({from.Mbr})", ct);
            await using var cmd = Cmd(src, "SELECT SRCSEQ, SRCDAT, SRCDTA FROM QTEMP.TOVCPYSRC ORDER BY SRCSEQ");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lines.Add([r.GetValue(0), r.GetValue(1), r.GetValue(2)]);
        }

        // Add the member only when it is missing: QCMDEXC errors do not reliably carry the CPF id, so do not rely on message text.
        var exists = SqlText.ToLong(await Scalar(target, "SELECT COUNT(*) FROM QSYS2.SYSPARTITIONSTAT WHERE TABLE_SCHEMA = ? AND TABLE_NAME = ? AND TABLE_PARTITION = ?", ct, to.Lib, to.File, to.Mbr)) > 0;
        if (!exists)
            await Exec(target, Ddl.Qcmdexc($"ADDPFM FILE({to.Lib}/{to.File}) MBR({to.Mbr}) SRCTYPE({(srcType.Length == 0 ? "*NONE" : SqlText.ClName(srcType))})"), ct);
        await Exec(target, $"CREATE OR REPLACE ALIAS QTEMP.TOVCPYDST FOR {to.Lib}.{to.File} ({to.Mbr})", ct);
        await Exec(target, "DELETE FROM QTEMP.TOVCPYDST", ct);
        foreach (var line in lines)
            await Exec(target, "INSERT INTO QTEMP.TOVCPYDST (SRCSEQ, SRCDAT, SRCDTA) VALUES (?, ?, ?)", ct, line);
        return lines.Count;
    }

    /// <summary>
    /// Copies one IFS stream file between partitions: IFS_READ_BINARY on the source in 32,000-byte chunks, then
    /// IFS_WRITE_BINARY on the target (first chunk replaces the file, the rest append; no end-of-line bytes added).
    /// </summary>
    private async Task<long> CopyStreamFileAsync(DbConnection target, PlanStep s, CancellationToken ct)
    {
        string from = s.CopyFrom ?? "", to = s.CopyTo ?? "";
        if (!TurnoverOptions.IsSafeIfsPath(from) || !TurnoverOptions.IsSafeIfsPath(to)) throw new TurnoverException($"Unsafe IFS path in copy step '{from}' → '{to}'.");
        var sourceEnv = options.Env(s.SourceEnvironment ?? throw new TurnoverException("Copy step without a source environment."));
        // Streamed chunk by chunk (source reader and target writer are separate connections): never the whole file in memory.
        const string write = "CALL QSYS2.IFS_WRITE_BINARY(PATH_NAME => ?, LINE => ?, FILE_CCSID => ?, OVERWRITE => ?, END_OF_LINE => 'NONE')";
        long bytes = 0; var first = true;
        await using (var src = await connections.OpenAsync(sourceEnv, ct))
        {
            await using var cmd = Cmd(src, "SELECT LINE FROM TABLE(QSYS2.IFS_READ_BINARY(PATH_NAME => ?, MAXIMUM_LINE_LENGTH => 32000, END_OF_LINE => 'NONE')) X ORDER BY LINE_NUMBER", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var chunk = r.GetValue(0) as byte[] ?? [];
                await Exec(target, write, ct, to, chunk, s.Ccsid ?? 1208, first ? "REPLACE" : "APPEND");
                first = false; bytes += chunk.Length;
            }
        }
        if (first) await Exec(target, write, ct, to, Array.Empty<byte>(), s.Ccsid ?? 1208, "REPLACE");   // an empty file still replaces the target
        return bytes;
    }

    private static DbCommand Cmd(DbConnection con, string sql, params object[] args)
    {
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var a in args) { var p = cmd.CreateParameter(); p.Value = a; cmd.Parameters.Add(p); }
        return cmd;
    }

    private static async Task Exec(DbConnection con, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Cmd(con, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> Scalar(DbConnection con, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Cmd(con, sql, args);
        return await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<DeploymentRun> RollbackAsync(TurnoverEnvironment target, IReadOnlyList<PlanStep> completed, string user, CancellationToken ct = default)
    {
        var run = new DeploymentRun { Mode = "Rollback", User = user, StartedAt = DateTimeOffset.UtcNow, Succeeded = true };
        DbConnection con;
        try { con = await connections.OpenAsync(target, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreachable replica must not stop the other environments' rollback: report it per step.
            run.Succeeded = false;
            foreach (var s in completed.OrderByDescending(x => x.Order))
                run.Results.Add(new StepResult { Order = s.Order, Title = "Undo: " + s.Title, Status = StepStatus.Failed, Message = $"Could not connect to {target.Key}: {ex.Message}" });
            run.EndedAt = DateTimeOffset.UtcNow;
            return run;
        }
        await using var _ = con;
        foreach (var s in completed.OrderByDescending(x => x.Order))
        {
            var result = new StepResult { Order = s.Order, Title = "Undo: " + s.Title };
            if (s.Rollback is null || DeploymentPlanner.IsManual(s.Rollback))
            {
                result.Status = StepStatus.Skipped;
                result.Message = s.Rollback is null ? "Nothing to undo." : "Manual: " + s.Rollback.TrimStart('-', ' ');
                run.Results.Add(result);
                continue;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                await using var cmd = con.CreateCommand();
                cmd.CommandText = s.Rollback.Trim().TrimEnd(';');
                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync(ct);
                result.Status = StepStatus.Succeeded; result.Message = "OK";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep going: a rollback that stops half-way leaves more damage than one that reports and continues.
                result.Status = StepStatus.Failed; result.Message = ex.Message; run.Succeeded = false;
            }
            result.DurationMs = sw.ElapsedMilliseconds;
            run.Results.Add(result);
        }
        run.EndedAt = DateTimeOffset.UtcNow;
        return run;
    }

}
