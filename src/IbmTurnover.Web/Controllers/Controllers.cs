using System.Text;
using IbmTurnover.Core;
using IbmTurnover.Core.Catalog;
using IbmTurnover.Core.Compare;
using IbmTurnover.Core.Models;
using IbmTurnover.Core.Planning;
using IbmTurnover.Core.Workflow;
using IbmTurnover.Web.Infrastructure;
using IbmTurnover.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IbmTurnover.Web.Controllers;

public abstract class TurnoverController(ICurrentUser currentUser) : Controller
{
    protected TurnoverUser Me => currentUser.User;
    protected bool IsDemo => currentUser.IsDemo;

    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        // The error page is reachable signed out; never let reading the user turn it into a second error.
        try { ViewData["User"] = currentUser.User; }
        catch (UnauthorizedAccessException) { ViewData["User"] = null; }
        ViewData["Demo"] = currentUser.IsDemo;
        ViewData["AuthMode"] = currentUser.Mode;
        base.OnActionExecuting(context);
    }

    private string SafeUser() { try { return currentUser.User.Name; } catch (UnauthorizedAccessException) { return "(anonymous)"; } }

    protected void Flash(string message, string kind = "good") { TempData["Flash"] = message; TempData["FlashKind"] = kind; }

    /// <summary>The failures an action expects: business refusals, catalog reads, the IBM i driver, the data folder.</summary>
    protected static bool Expected(Exception ex) =>
        ex is TurnoverException or CatalogLoadException or System.Data.Common.DbException or IOException;

    /// <summary>Logs a failure with where and who: refusals at Information (expected), everything else at Warning with the
    /// exception. One place, so no catch block in a controller swallows an error silently.</summary>
    protected void Log(Exception ex)
    {
        var log = HttpContext.RequestServices.GetRequiredService<ILogger<TurnoverController>>();
        if (ex is TurnoverException)
            log.LogInformation("Refused {Path} for {User}: {Reason}", HttpContext.Request.Path, SafeUser(), ex.Message);
        else
            log.LogWarning(ex, "{Kind} on {Path} for {User}", ex.GetType().Name, HttpContext.Request.Path, SafeUser());
    }

    /// <summary>Logs it and shows it. Driver and file-system messages can name hosts, profiles and paths: users get a
    /// neutral sentence, the log gets the detail.</summary>
    protected void Report(Exception ex)
    {
        Log(ex);
        Flash(ex switch
        {
            TurnoverException t => t.Message,
            CatalogLoadException c => c.Message + " See Workflow for the failing check.",
            System.Data.Common.DbException => "IBM i refused the request or could not be reached. The details are in the application log.",
            _ => "The tool's data folder could not be read or written. The details are in the application log.",
        }, "danger");
    }

    /// <summary>Business-rule, catalog, driver and data-folder failures become a message on the page, not an error page.</summary>
    protected async Task<IActionResult> Guard(Func<Task<IActionResult>> action, Func<IActionResult> onError)
    {
        try { return await action(); }
        catch (Exception ex) when (Expected(ex)) { Report(ex); return onError(); }
    }
}

public sealed class HomeController(ICurrentUser user, TurnoverService svc, ILogger<HomeController> log) : TurnoverController(user)
{
    public IActionResult Index() => View(new DashboardViewModel
    {
        Environments = svc.Options.Environments.OrderBy(e => e.Tier).ToList(),
        Packages = svc.Packages(),
        IsLive = svc.Options.IsLive,
    });

    /// <summary>Drift between two environments, loaded by the dashboard after the page renders (live catalogs can be slow).</summary>
    [HttpGet]
    public async Task<IActionResult> Drift(string source, string target, CancellationToken ct)
    {
        try
        {
            var r = await svc.CompareAsync(source, target, null, ct);
            // Same numbers, same definitions as the summary tiles on the Compare page this links to.
            return DriftJson(new DriftSummary(source, target, r.Differences.Count,
                r.Differences.Count(d => d.HasOpenBlocker) + r.GlobalFindings.Count(f => f.Severity == Severity.Blocker),
                r.Differences.Count(d => d.Severity == Severity.Warning),
                r.Differences.GroupBy(d => Ui.KindLabel(d.Kind)).ToDictionary(g => g.Key, g => g.Count()), null));
        }
        catch (Exception ex) when (ex is TurnoverException or CatalogLoadException or System.Data.Common.DbException)
        {
            // Driver messages can name hosts and libraries: log them, show a neutral message.
            log.LogWarning(ex, "Drift {Source}->{Target} failed", source, target);
            return DriftJson(new DriftSummary(source, target, 0, 0, 0, new Dictionary<string, int>(), "Could not read the catalog. See Workflow."));
        }
    }

    // Serialized up front (not streamed) so the response works on every host, including TestServer under roll-forward.
    private ContentResult DriftJson(DriftSummary s) =>
        Content(System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), "application/json");

    [HttpPost]
    public IActionResult SwitchUser(string name, string? returnUrl)
    {
        // Only in demo mode, and only to one of the known demo users.
        if (IsDemo && CurrentUser.DemoUsers.Any(u => u.Name == name))
            Response.Cookies.Append(CurrentUser.Cookie, name, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "~/");
    }

    /// <summary>Shown by the exception handler; the request id matches the entry in the log folder.</summary>
    [AllowAnonymous]
    public IActionResult Error()
    {
        ViewData["RequestId"] = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        return View();
    }
}

[AllowAnonymous]
public sealed class AccountController : Controller
{
    /// <summary>Status pages (401/403/404): signed in without a Turnover role lands here.</summary>
    public IActionResult Status(int code)
    {
        Response.StatusCode = code is >= 400 and < 600 ? code : 400;
        ViewData["Title"] = code switch { 403 => "No access", 401 => "Sign-in required", 404 => "Not found", _ => "Request failed" };
        ViewData["Code"] = code;
        return View("Status");
    }

    public IActionResult AccessDenied() => Status(403);

    [HttpPost]
    public IActionResult SignOut([FromServices] AuthSettings auth)
    {
        if (auth.Mode == AuthMode.EntraId)
            return SignOut(new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = Url.Content("~/") },
                Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme);
        return Redirect(Url.Content("~/"));
    }
}

public sealed class CompareController(ICurrentUser user, TurnoverService svc, SnapshotService snapshots, CheckoutService checkouts) : TurnoverController(user)
{
    private const int LazyThreshold = 150;

    public async Task<IActionResult> Index([FromQuery] CompareQuery q, CancellationToken ct)
    {
        var envs = svc.Options.Environments.OrderBy(e => e.Tier).ToList();
        q.Normalize(envs);
        ComparisonResult? result = null;
        try { result = await RunAsync(q, ct); }
        catch (Exception ex) when (Expected(ex)) { Report(ex); }

        var holders = result is null ? new Dictionary<string, string>()
            : CheckoutService.Members(result, result.Differences.Select(d => d.Id))
                .Select(m => (m.DiffId, Holder: checkouts.Holder(m.Key)?.User)).Where(x => x.Holder is not null)
                .GroupBy(x => x.DiffId).ToDictionary(g => g.Key, g => g.First().Holder!, StringComparer.OrdinalIgnoreCase);
        return View(new CompareViewModel
        {
            Environments = envs, Query = q, Result = result, LazyDiffs = result?.Differences.Count > LazyThreshold, Holders = holders,
            AllLogicals = envs.SelectMany(e => e.Libraries.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
            SourceHistory = SafeHistory(q.Mode == "library" ? q.LibEnv : q.Source),
            TargetHistory = SafeHistory(q.Target),
        });
    }

    /// <summary>Attribute-level change report as CSV (opens in Excel).</summary>
    public Task<IActionResult> Csv([FromQuery] CompareQuery q, CancellationToken ct) =>
        Guard(async () =>
        {
            q.Normalize(svc.Options.Environments);
            var r = await RunAsync(q, ct) ?? throw new TurnoverException("Nothing to export: run a comparison first.");
            return File(ChangeReport.Csv(r), "text/csv", $"turnover-{r.Source.Key}-{r.Target.Key}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        }, () => LocalRedirect("~" + q.Url("/Compare")));

    /// <summary>Side-by-side definition for one difference, fetched when a card is opened on large comparisons.</summary>
    public async Task<IActionResult> Diff([FromQuery] CompareQuery q, string id, CancellationToken ct)
    {
        q.Normalize(svc.Options.Environments);
        ComparisonResult? r;
        try { r = await RunAsync(q, ct); }
        catch (Exception ex) when (Expected(ex)) { Log(ex); return NotFound(); }
        var d = r?.Find(id ?? "");
        if (d is null) return NotFound();
        return PartialView("_SideBySide", (d.TargetDdl, d.SourceDdl, $"{q.TargetKey} · now ({d.TargetLibrary})", $"After promotion (from {q.SourceKey})"));
    }

    [HttpPost]
    public Task<IActionResult> CreatePackage(CompareQuery q, string[] selected, string title, string ticket, CancellationToken ct) =>
        Guard(async () =>
        {
            q.Normalize(svc.Options.Environments);
            if (q.SourceSnapshot is not null || q.TargetSnapshot is not null)
                throw new TurnoverException("Packages are built from live environments only. Re-run the comparison against Live.");
            var pair = q.Mode == "library" ? new LibraryPair(q.FromLib, q.FromLib.ToUpperInvariant(), q.ToLib.ToUpperInvariant()) : null;
            var p = await svc.CreatePackageAsync(Me, q.SourceKey, q.TargetKey, q.Logicals, selected ?? [], title, ticket, pair, ct, q.SourceOnly, q.PgmLib, q.SrcLib);
            Flash($"{p.Code} created with {p.SelectedDiffIds.Count} change(s). Review the plan, then submit it for approval.");
            return RedirectToAction("Details", "Packages", new { id = p.Number });
        }, () => LocalRedirect("~" + q.Url("/Compare")));

    /// <summary>Checks out, for the signed-in developer, every source member this comparison would change.</summary>
    [HttpPost]
    public Task<IActionResult> CheckOutAll(CompareQuery q, CancellationToken ct) =>
        Guard(async () =>
        {
            q.Normalize(svc.Options.Environments);
            var r = await RunAsync(q, ct) ?? throw new TurnoverException("Run the comparison first.");
            if (!Me.IsIn(Roles.Developer)) throw new TurnoverException("Only developers check out source.");
            var (n, conflicts) = checkouts.CheckOutAll(Me, r, $"Turnover {r.Source.Key} → {r.Target.Key}");
            Flash(conflicts.Count == 0 ? $"{n} member(s) checked out to {Me.Name}." : $"{n} checked out. Held by others: {string.Join(", ", conflicts.Take(5))}.", conflicts.Count == 0 ? "good" : "danger");
            return LocalRedirect("~" + q.Url("/Compare"));
        }, () => LocalRedirect("~" + q.Url("/Compare")));

    private async Task<ComparisonResult?> RunAsync(CompareQuery q, CancellationToken ct)
    {
        if (q.Mode == "library")
            return string.IsNullOrWhiteSpace(q.FromLib) || string.IsNullOrWhiteSpace(q.ToLib) ? null
                : await svc.CompareLibrariesAsync(q.LibEnv!, q.FromLib, q.ToLib, ct, q.SourceOnly, q.Refresh, q.PgmLib, q.SrcLib);
        if (!q.Run) return null;
        return await svc.CompareAsync(q.Source, q.Target, q.Logicals, ct, q.SourceSnapshot, q.TargetSnapshot, q.Refresh);
    }

    private IReadOnlyList<SnapshotRecord> SafeHistory(string env)
    {
        try { return snapshots.History(env).Take(40).ToList(); }
        catch (Exception ex) when (Expected(ex)) { Log(ex); return []; }
    }
}

public sealed class SnapshotsController(ICurrentUser user, TurnoverService svc, SnapshotService snapshots) : TurnoverController(user)
{
    public IActionResult Index(string? env)
    {
        var envs = svc.Options.Environments.OrderBy(e => e.Tier).ToList();
        var key = envs.FirstOrDefault(e => e.Key.Equals(env, StringComparison.OrdinalIgnoreCase))?.Key ?? envs[0].Key;
        return View(new SnapshotsViewModel
        {
            Environments = envs, Env = key, History = snapshots.History(key),
            Settings = svc.Options.Snapshots, Counts = envs.ToDictionary(e => e.Key, e => snapshots.History(e.Key).Count),
        });
    }

    [HttpPost]
    public Task<IActionResult> Capture(string env, string? label, CancellationToken ct) =>
        Guard(async () =>
        {
            var rec = await snapshots.CaptureAsync(env, Me.Name, label, "Manual", ct);
            Flash(rec.Unchanged ? $"Captured {rec.Environment}: no structural change since the previous snapshot." : $"Captured {rec.Environment}: {rec.Objects:N0} objects{(rec.ChangesFromPrevious is int n ? $", {n} change(s) since the previous snapshot" : "")}.");
            return RedirectToAction(nameof(Index), new { env });
        }, () => RedirectToAction(nameof(Index), new { env }));
}

public sealed class WorkflowController(ICurrentUser user, TurnoverService svc, CatalogService catalog) : TurnoverController(user)
{
    public IActionResult Index(string? env)
    {
        var envs = svc.Options.Environments.OrderBy(e => e.Tier).ToList();
        var key = envs.FirstOrDefault(e => e.Key.Equals(env, StringComparison.OrdinalIgnoreCase))?.Key ?? envs[0].Key;
        return View(new WorkflowViewModel
        {
            Environments = envs, Env = key, Report = catalog.LastReport(key), IsLive = svc.Options.IsLive,
            Packages = svc.Packages(), Options = svc.Options,
        });
    }

    /// <summary>Re-reads one environment now (bypasses the cache) so every check shows fresh status and timing.</summary>
    [HttpPost]
    public async Task<IActionResult> Read(string env, CancellationToken ct)
    {
        try
        {
            await catalog.LoadAsync(svc.Options.Env(env), ct, refresh: true);
            Flash($"{env} read from {(svc.Options.IsLive ? "IBM i" : "the sample catalog")}.");
        }
        catch (CatalogLoadException ex) { Log(ex); Flash($"{env}: at least one check failed — see the red boxes. Nothing is compared until every check passes.", "danger"); }
        catch (Exception ex) when (Expected(ex)) { Report(ex); }
        return RedirectToAction(nameof(Index), new { env });
    }
}

public sealed class CheckoutsController(ICurrentUser user, TurnoverService svc, CheckoutService checkouts) : TurnoverController(user)
{
    public IActionResult Index(bool history = false) => View(new CheckoutsViewModel
    {
        Environments = svc.Options.Environments.OrderBy(e => e.Tier).ToList(), Items = checkouts.All().Where(c => history || c.Active).ToList(),
        History = history, Settings = svc.Options.Checkout,
    });

    [HttpPost]
    public IActionResult CheckOut(string env, string library, string file, string member, string? reason)
    {
        try { var c = checkouts.CheckOut(Me, env, library ?? "", file ?? "", member ?? "", reason); Flash($"{c.Key} is checked out to {c.User}."); }
        catch (Exception ex) when (Expected(ex)) { Report(ex); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult Release(string id, string? note)
    {
        try { var c = checkouts.Release(Me, id, note); Flash($"{c.Key} released."); }
        catch (Exception ex) when (Expected(ex)) { Report(ex); }
        return RedirectToAction(nameof(Index));
    }
}

public sealed class SourceController(ICurrentUser user, TurnoverService svc, SourceArchiveService sources) : TurnoverController(user)
{
    /// <summary>Archived versions of one member; compare any two (a = older, b = newer).</summary>
    public IActionResult Index(string? key, string? a, string? b)
    {
        var all = sources.Archive.All();
        var keys = all.GroupBy(v => v.Key).Select(g => (Key: g.Key, Count: g.Count(), Last: g.Max(v => v.CapturedAt))).OrderByDescending(x => x.Last).ToList();
        var versions = key is null ? [] : sources.Archive.Versions(key);
        SourceVersion? va = a is null ? null : sources.Archive.Get(a), vb = b is null ? null : sources.Archive.Get(b);
        if (va is not null && !va.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) va = null;
        if (vb is not null && !vb.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) vb = null;
        return View(new SourceViewModel
        {
            Environments = svc.Options.Environments.OrderBy(e => e.Tier).ToList(), Keys = keys, Key = key, Versions = versions,
            A = va, B = vb, TextA = va is null ? null : sources.Archive.Text(va), TextB = vb is null ? null : sources.Archive.Text(vb),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Capture(string env, string library, string file, string member, CancellationToken ct)
    {
        try
        {
            var v = await sources.CaptureAsync(Me, env, library ?? "", file ?? "", member ?? "", ct);
            if (v is null) { Flash($"{library}/{file}({member}) was not found in {env}.", "danger"); return RedirectToAction(nameof(Index)); }
            Flash($"Archived {v.Member} from {env} ({v.Lines} lines).");
            return RedirectToAction(nameof(Index), new { key = v.Key });
        }
        catch (Exception ex) when (Expected(ex)) { Report(ex); return RedirectToAction(nameof(Index)); }
    }
}

public sealed class PackagesController(ICurrentUser user, TurnoverService svc) : TurnoverController(user)
{
    public IActionResult Index(string? status)
    {
        var list = svc.Packages();
        if (Enum.TryParse<PackageStatus>(status, true, out var s)) list = list.Where(p => p.Status == s).ToList();
        ViewData["Status"] = status;
        return View(list);
    }

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        TurnoverPackage p;
        try { p = svc.Package(id); }
        catch (TurnoverException) { return NotFound(); }

        ComparisonResult cmp; DeploymentPlan plan; string? error = null;
        try { (cmp, plan) = await svc.AnalyzeAsync(p, ct); }
        catch (Exception ex) when (Expected(ex))
        {
            Log(ex);
            error = ex is TurnoverException ? ex.Message : "A catalog read failed (see Workflow).";
            cmp = new ComparisonResult();
            plan = new DeploymentPlan { Steps = p.ApprovedSteps };
        }
        // After approval, show what was approved (frozen), not a fresh plan.
        var steps = p.ApprovedSteps.Count > 0 ? p.ApprovedSteps : plan.Steps;
        var frozen = new DeploymentPlan { Steps = steps, Findings = plan.Findings, BackupLibrary = p.BackupLibrary, ScriptHash = p.ApprovedScriptHash ?? plan.ScriptHash };
        // Before/after source for approvers: what was archived at approval, otherwise read live now.
        IReadOnlyList<SourceChange> source = [];
        if (error is null)
        {
            try
            {
                source = p.ApprovedSteps.Count > 0 ? svc.Sources.ArchivedChanges(p, cmp) : await svc.Sources.LiveChangesAsync(p, cmp, ct);
            }
            catch (Exception ex) when (Expected(ex)) { Log(ex); source = []; }
        }
        return View(new PackageViewModel
        {
            Package = p, Comparison = cmp, Plan = frozen, User = Me, ApprovalsRequired = svc.ApprovalsRequired(p), AnalysisError = error,
            Script = p.ApprovedScript ?? (error is null ? TurnoverService.Script(p, cmp, plan) : ""),
            Rollback = DeploymentPlanner.RenderRollback(steps, p.Code),
            SourceChanges = source,
            Windows = svc.Windows(p.TargetEnv),
            WindowOpen = IbmTurnover.Core.Workflow.DeployWindow.IsOpen(svc.Windows(p.TargetEnv), svc.LocalNow()),
            NextOpen = IbmTurnover.Core.Workflow.DeployWindow.NextOpen(svc.Windows(p.TargetEnv), svc.LocalNow()),
            ScheduledLocal = p.ScheduledFor is { } at ? svc.Local(at) : null,
            Distribution = svc.Options.Env(p.TargetEnv).Distribution,
        });
    }

    [HttpPost] public Task<IActionResult> Submit(int id, string? justification, CancellationToken ct) =>
        Act(id, async () => { await svc.SubmitAsync(Me, id, justification, ct); return "Submitted for approval."; });

    [HttpPost] public Task<IActionResult> Approve(int id, string? comment, CancellationToken ct) =>
        Act(id, async () => { var p = await svc.DecideAsync(Me, id, true, comment ?? "", ct); return p.Status == PackageStatus.Approved ? "Approved. The script is now frozen." : "Approval recorded; more approvals are needed."; });

    [HttpPost] public Task<IActionResult> Reject(int id, string? comment, CancellationToken ct) =>
        Act(id, async () => { await svc.DecideAsync(Me, id, false, comment ?? "", ct); return "Rejected. The requester can change the selection and resubmit."; });

    [HttpPost] public Task<IActionResult> DryRun(int id, CancellationToken ct) =>
        Act(id, async () => { var r = await svc.DryRunAsync(Me, id, ct); return $"Dry run finished: {r.Results.Count} steps walked, nothing sent to IBM i."; });

    [HttpPost] public Task<IActionResult> Deploy(int id, string? emergency, CancellationToken ct) =>
        Act(id, async () =>
        {
            var r = await svc.DeployAsync(Me, id, ct, emergency);
            return r.Succeeded ? $"Deployed ({r.Mode}). {r.Results.Count} steps processed." : "Deployment stopped at a failed step. Finally steps still ran. See the run log; roll back if needed.";
        });

    /// <param name="when">Server-local date and time from a datetime-local input.</param>
    [HttpPost] public Task<IActionResult> Schedule(int id, DateTime? when) =>
        Act(id, () =>
        {
            if (when is null) throw new TurnoverException("Pick a date and time.");
            // The hour skipped when clocks go forward does not exist; converting it would throw.
            if (TimeZoneInfo.Local.IsInvalidTime(DateTime.SpecifyKind(when.Value, DateTimeKind.Unspecified)))
                throw new TurnoverException($"{when:yyyy-MM-dd HH:mm} does not exist on this server (clocks go forward then). Pick another time.");
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(when.Value, DateTimeKind.Unspecified), TimeZoneInfo.Local);
            var p = svc.Schedule(Me, id, new DateTimeOffset(utc, TimeSpan.Zero));
            return Task.FromResult($"Scheduled for {svc.Local(p.ScheduledFor!.Value):ddd yyyy-MM-dd HH:mm} (server time).");
        });

    [HttpPost] public Task<IActionResult> MarkFailed(int id, string? note) =>
        Act(id, () => { svc.MarkFailed(Me, id, note ?? ""); return Task.FromResult("Marked failed. Roll back or fix by hand."); });

    [HttpPost] public Task<IActionResult> Unschedule(int id) =>
        Act(id, () => { svc.Unschedule(Me, id); return Task.FromResult("Schedule cancelled."); });

    [HttpPost] public Task<IActionResult> VerifyReplicas(int id, CancellationToken ct) =>
        Act(id, async () =>
        {
            var runs = await svc.VerifyReplicasAsync(Me, id, ct);
            return string.Join(" ", runs.Select(r => r.Succeeded ? $"{r.Environment}: in step." : $"{r.Environment}: {r.Results.Count(x => x.Status == StepStatus.Failed)} object(s) differ."));
        });

    [HttpPost] public Task<IActionResult> AddStep(int id, CustomStepPoint point, StepKind kind, string? title, string? command, string? rollback) =>
        Act(id, () => { svc.AddCustomStep(Me, id, point, kind, title ?? "", command ?? "", rollback); return Task.FromResult("Custom step added. The package is back in draft."); });

    [HttpPost] public Task<IActionResult> RemoveStep(int id, string stepId) =>
        Act(id, () => { svc.RemoveCustomStep(Me, id, stepId); return Task.FromResult("Custom step removed."); });

    [HttpPost] public Task<IActionResult> Rollback(int id, CancellationToken ct) =>
        Act(id, async () => { var r = await svc.RollbackAsync(Me, id, ct); return r.Succeeded ? "Rolled back." : "Rollback finished with errors. See the run log."; });

    [HttpPost] public Task<IActionResult> Selection(int id, string[] selected, CancellationToken ct) =>
        Act(id, async () => { await svc.UpdateSelectionAsync(Me, id, selected ?? [], ct); return "Selection updated."; });

    public async Task<IActionResult> Script(int id, bool rollback, CancellationToken ct)
    {
        TurnoverPackage p;
        try { p = svc.Package(id); }
        catch (TurnoverException) { return NotFound(); }
        string text;
        try
        {
            if (rollback)
                text = DeploymentPlanner.RenderRollback(p.ApprovedSteps.Count > 0 ? p.ApprovedSteps : (await svc.AnalyzeAsync(p, ct)).Plan.Steps, p.Code);
            else if (p.ApprovedScript is not null) text = p.ApprovedScript;
            else { var (cmp, plan) = await svc.AnalyzeAsync(p, ct); text = TurnoverService.Script(p, cmp, plan); }
        }
        catch (Exception ex) when (Expected(ex))
        {
            Log(ex);
            Flash("The script cannot be built right now: " + (ex is System.Data.Common.DbException ? "IBM i could not be read." : ex.Message), "danger");
            return RedirectToAction(nameof(Details), new { id });
        }
        return File(Encoding.UTF8.GetBytes(text), "application/sql", $"{p.Code}{(rollback ? "-rollback" : "")}.sql");
    }

    private Task<IActionResult> Act(int id, Func<Task<string>> action) =>
        Guard(async () => { Flash(await action()); return RedirectToAction(nameof(Details), new { id }); },
              () => RedirectToAction(nameof(Details), new { id }));
}

public sealed class EnvironmentsController(ICurrentUser user, TurnoverService svc, CatalogService catalog) : TurnoverController(user)
{
    public IActionResult Index() => View(Model());

    [HttpPost]
    public async Task<IActionResult> Test(string env, CancellationToken ct)
    {
        try
        {
            var e = svc.Options.Env(env);
            return View(nameof(Index), Model(e.Key, await catalog.Provider.TestAsync(e, ct)));
        }
        catch (Exception ex) when (Expected(ex)) { Report(ex); return RedirectToAction(nameof(Index)); }
    }

    private EnvironmentsViewModel Model(string? tested = null, IReadOnlyList<(string, bool, string)>? results = null) => new()
    {
        Environments = svc.Options.Environments.OrderBy(e => e.Tier).ToList(),
        Logicals = svc.Options.Environments.SelectMany(e => e.Libraries.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
        ProfileLogicals = svc.Options.Environments.SelectMany(e => e.Profiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
        TrackedProfiles = svc.Options.TrackedProfiles,
        IsLive = svc.Options.IsLive, TestedEnv = tested, TestResults = results ?? [],
    };
}

public sealed class LibraryMoveController(ICurrentUser user, TurnoverService svc, LibraryMoveAnalyzer analyzer) : TurnoverController(user)
{
    public async Task<IActionResult> Index(string? env, string? from, string? to, bool keepOriginal = true, CancellationToken ct = default)
    {
        var envs = svc.Options.Environments.OrderBy(e => e.Tier).ToList();
        LibraryMoveResult? result = null;
        if (!string.IsNullOrWhiteSpace(env) && !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
        {
            try { result = await analyzer.AnalyzeAsync(env, from, to, keepOriginal, ct); }
            catch (Exception ex) when (Expected(ex)) { Report(ex); }
        }
        return View(new LibraryMoveViewModel { Environments = envs, Env = env ?? envs[0].Key, From = from ?? "", To = to ?? "", KeepOriginal = keepOriginal, Result = result });
    }
}
