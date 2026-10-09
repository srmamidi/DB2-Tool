using IbmTurnover.Core;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Web.Infrastructure;

/// <summary>
/// Captures every environment every Turnover:Snapshots:AutoCaptureMinutes (0 = off). One environment failing is
/// logged and does not stop the others or the app.
/// </summary>
public sealed class AutoSnapshotService(SnapshotService snapshots, TurnoverOptions options, ILogger<AutoSnapshotService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Snapshots.AutoCaptureMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Snapshots.AutoCaptureMinutes));
        do
        {
            foreach (var env in options.Environments)
            {
                try
                {
                    var rec = await snapshots.CaptureAsync(env.Key, "system", "Scheduled", "Auto", stoppingToken);
                    log.LogInformation("Snapshot {Env} {Id}: {Changes} change(s)", env.Key, rec.Id, rec.ChangesFromPrevious);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Scheduled snapshot of {Env} failed", env.Key);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
