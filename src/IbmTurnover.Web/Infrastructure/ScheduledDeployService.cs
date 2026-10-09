using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Web.Infrastructure;

/// <summary>
/// Once a minute, deploys approved packages whose scheduled time has come. Each check runs in its own scope (the
/// workflow service is scoped). A refused deploy is recorded on the package; nothing here stops the app.
/// </summary>
public sealed class ScheduledDeployService(IServiceScopeFactory scopes, IbmTurnover.Core.TurnoverOptions options, ILogger<ScheduledDeployService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Turnover:Scheduler:IntervalSeconds (validated 10-3600 at startup).
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Scheduler.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var n = await scope.ServiceProvider.GetRequiredService<TurnoverService>().RunDueAsync(stoppingToken);
                if (n > 0) log.LogInformation("Scheduled deploy: {Count} package(s) deployed", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Scheduled deploy check failed");
            }
        }
    }
}
