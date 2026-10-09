using Microsoft.Extensions.Logging;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IbmTurnover.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the turnover engine to any ASP.NET Core (MVC) app. Reads the "Turnover" section and validates it
    /// immediately: a bad library map throws here, at startup, instead of deploying into the wrong library.
    /// </summary>
    public static IServiceCollection AddIbmTurnover(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        var options = configuration.GetSection(TurnoverOptions.Section).Get<TurnoverOptions>() ?? new TurnoverOptions();
        options.Validate(name => configuration.GetConnectionString(name));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);

        Func<string, string?> cs = name => configuration.GetConnectionString(name);
        if (options.IsLive)
        {
            services.AddSingleton<IDb2ConnectionFactory>(_ => new OdbcConnectionFactory(cs));
            services.AddSingleton<ICatalogProvider, Db2iCatalogProvider>();
            services.AddSingleton<IDeploymentExecutor, Db2DeploymentExecutor>();
        }
        else
        {
            services.AddSingleton<ICatalogProvider, SampleCatalogProvider>();
            services.AddSingleton<IDeploymentExecutor, SimulatedExecutor>();
        }
        services.AddSingleton<SimulatedExecutor>();
        services.AddSingleton<CatalogService>();
        var dataDir = Path.IsPathRooted(options.DataDirectory) ? options.DataDirectory : Path.Combine(contentRoot, options.DataDirectory);
        services.AddSingleton<ITurnoverRepository>(_ => new JsonFileTurnoverRepository(dataDir));
        services.AddSingleton<ISnapshotStore>(_ => new FileSnapshotStore(dataDir));
        services.AddSingleton(sp =>
        {
            var snaps = new SnapshotService(sp.GetRequiredService<ISnapshotStore>(), sp.GetRequiredService<CatalogService>(), options)
            {
                CountChanges = (env, before, after) => TurnoverService.CompareHistory(options, options.Env(env), before, after).Differences.Count,
            };
            // Every fresh catalog read becomes a point in history (identical structure is recorded without a new copy).
            if (options.Snapshots.CaptureOnCompare)
                sp.GetRequiredService<CatalogService>().Loaded += s =>
                {
                    // History is a by-product: a failed save (disk full, App_Data not writable) is logged, never a failed comparison.
                    try { snaps.Record(s, "system", null, "Compare"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TurnoverException or System.Text.Json.JsonException)
                    {
                        sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("IbmTurnover.Snapshots")
                            .LogWarning(ex, "Could not record a snapshot of {Env}", s.EnvironmentKey);
                    }
                    return Task.CompletedTask;
                };
            return snaps;
        });
        services.AddSingleton<ICheckoutStore>(_ => new JsonCheckoutStore(dataDir));
        services.AddSingleton<CheckoutService>();
        services.AddSingleton<ISourceArchive>(_ => new FileSourceArchive(dataDir));
        services.AddSingleton<SourceArchiveService>();
        services.AddScoped<TurnoverService>();
        services.AddScoped<LibraryMoveAnalyzer>();
        return services;
    }
}
