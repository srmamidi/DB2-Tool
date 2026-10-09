using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Execution;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Workflow;

namespace IbmTurnover.Tests;

/// <summary>A clock the test moves by hand; local time is UTC so deploy windows are predictable.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>Same four environments as the web app's web.config, wired to the sample catalog.</summary>
internal static class TestEnv
{
    public static TurnoverOptions Options()
    {
        TurnoverEnvironment E(string key, EnvironmentTier tier, string host, string sfx) => new()
        {
            Key = key, DisplayName = key, Tier = tier, Host = host,
            LibrariesText = $"CUSTOMER=CUS{sfx};ORDERS=ORD{sfx};ORDPGM=ORDPGM{sfx};ORDSRC=ORDSRC{sfx}",
            ProfilesText = $"ORDER_SVC=SVCORD{sfx};SHIP_SVC=SVCSHP{sfx};ORDER_GRP=ORDUSERS",
        };
        var o = new TurnoverOptions
        {
            Mode = "Sample",
            Environments =
            [
                E("DEV", EnvironmentTier.Dev, "IBMDEV01", "DEV"),
                E("QA", EnvironmentTier.QA, "IBMDEV01", "QA"),
                E("STAGE", EnvironmentTier.Stage, "IBMSTG01", "STG"),
                E("PROD", EnvironmentTier.Prod, "IBMPRD01", "PRD"),
            ],
        };
        o.Validate();
        return o;
    }

    public static string TempDir() => Path.Combine(Path.GetTempPath(), "tov-tests-" + Guid.NewGuid().ToString("N"));

    public static CatalogService Catalog(TurnoverOptions o) => new(new SampleCatalogProvider(), o, TimeProvider.System);

    public static TurnoverService Service(out JsonFileTurnoverRepository repo, TurnoverOptions? options = null) =>
        Service(out repo, out _, options);

    public static TurnoverService Service(out JsonFileTurnoverRepository repo, out FileSnapshotStore snapshots, TurnoverOptions? options = null) =>
        Service(out repo, out snapshots, out _, out _, options);

    public static TurnoverService Service(out JsonFileTurnoverRepository repo, out FileSnapshotStore snapshots, out CheckoutService checkouts,
        out FileSourceArchive archive, TurnoverOptions? options = null, IDeploymentExecutor? executor = null, CatalogService? catalog = null, TimeProvider? clock = null)
    {
        options ??= Options();
        clock ??= TimeProvider.System;
        var dir = TempDir();
        repo = new JsonFileTurnoverRepository(dir);
        snapshots = new FileSnapshotStore(dir);
        catalog ??= Catalog(options);
        checkouts = new CheckoutService(new JsonCheckoutStore(dir), options, clock);
        archive = new FileSourceArchive(dir);
        var sources = new SourceArchiveService(archive, catalog, options, clock);
        var sim = new SimulatedExecutor();
        return new TurnoverService(Microsoft.Extensions.Options.Options.Create(options), catalog, repo, executor ?? sim, sim, snapshots, checkouts, sources, clock);
    }

    public static readonly TurnoverUser Dev = new("alice", [Roles.Developer]);
    public static TurnoverEnvironment Env(string key, EnvironmentTier tier, string host, string sfx) => new()
    {
        Key = key, DisplayName = key, Tier = tier, Host = host,
        LibrariesText = $"CUSTOMER=CUS{sfx};ORDERS=ORD{sfx};ORDPGM=ORDPGM{sfx};ORDSRC=ORDSRC{sfx}",
        ProfilesText = $"ORDER_SVC=SVCORD{sfx};SHIP_SVC=SVCSHP{sfx};ORDER_GRP=ORDUSERS",
    };
    public static readonly TurnoverUser Approver1 = new("bob", [Roles.Approver]);
    public static readonly TurnoverUser Approver2 = new("carol", [Roles.Approver]);
    public static readonly TurnoverUser Release = new("dave", [Roles.ReleaseManager]);
}
