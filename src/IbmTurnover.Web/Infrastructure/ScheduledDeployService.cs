using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Web.Infrastructure;

/// <summary>
/// Once a minute, deploys approved packages whose scheduled time has come. Each check runs in its own scope (the
/// workflow service is scoped). A refused deploy is recorded on the package; nothing here stops the app.
/// </summary>
public sealed class ScheduledDeployService(IServiceScopeFactory scopes, ILogger<ScheduledDeployService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
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
