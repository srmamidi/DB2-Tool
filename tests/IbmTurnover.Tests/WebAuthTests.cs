using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IbmTurnover.Tests;

/// <summary>Signs a request in from headers: X-Test-User and X-Test-Groups (AD group names, ';' separated).</summary>
internal sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        claims.AddRange(Request.Headers["X-Test-Groups"].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(g => new Claim(ClaimTypes.Role, g)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
    }
}

/// <summary>The app as production runs it: Windows login, roles from the AD groups in web.config (CORP\TOV-*).</summary>
public sealed class WindowsFactory : WebApplicationFactory<Program>
{
    private readonly string? _old = Environment.GetEnvironmentVariable("Turnover__Auth__Mode");

    public WindowsFactory()
    {
        Environment.SetEnvironmentVariable("Turnover__Auth__Mode", "Windows");
        Environment.SetEnvironmentVariable("Turnover__DataDirectory", WebFactory.SharedDataDir);
        Environment.SetEnvironmentVariable("Logging__File__Path", WebFactory.LogDir);
        // Behave as under IIS (IIS authenticates): the Kerberos handler used outside IIS cannot run on the in-memory test server.
        Environment.SetEnvironmentVariable("APP_POOL_ID", "TurnoverTests");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(s => s.AddAuthentication(o =>
        {
            o.DefaultScheme = "Test"; o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; o.DefaultForbidScheme = "Test";
        }).AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Test", _ => { }));
    }

    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("Turnover__Auth__Mode", _old);
        Environment.SetEnvironmentVariable("APP_POOL_ID", null);
        base.Dispose(disposing);
    }
}

[Collection("web")]
public class WebAuthTests(WindowsFactory factory) : IClassFixture<WindowsFactory>
{
    private HttpClient As(string? user, string groups = "")
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null) { c.DefaultRequestHeaders.Add("X-Test-User", user); c.DefaultRequestHeaders.Add("X-Test-Groups", groups); }
        return c;
    }

    [Fact]
    public async Task Signed_in_without_a_turnover_group_is_refused_and_a_member_gets_in()
    {
        var anon = await As(null).GetAsync("/Packages");
        Assert.True(anon.StatusCode == HttpStatusCode.Unauthorized, $"{anon.StatusCode}: {(await anon.Content.ReadAsStringAsync())[..Math.Min(1500, (await anon.Content.ReadAsStringAsync()).Length)]}");
        Assert.Equal(HttpStatusCode.Forbidden, (await As(@"CORP\eve", @"CORP\Domain Users").GetAsync("/Packages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(@"CORP\alice", @"CORP\TOV-Developers").GetAsync("/Packages")).StatusCode);
    }

    [Fact]
    public async Task Demo_user_switching_does_nothing_in_windows_mode()
    {
        var html = await (await As(@"CORP\alice", @"CORP\TOV-Developers").GetAsync("/")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("SwitchUser", html);
        Assert.Contains(@"CORP\alice", html);
    }
}

[Collection("web")]
public class WebHostingTests(WebFactory factory) : IClassFixture<WebFactory>
{
    /// <summary>Installed as an IIS application under /turnover, every link must keep that prefix.</summary>
    [Fact]
    public async Task Links_and_redirects_keep_the_iis_application_path()
    {
        using var sub = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, PathBaseFilter>()));
        var c = sub.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { "/turnover/", "/turnover/Packages", "/turnover/Compare?source=DEV&target=QA", "/turnover/Snapshots?env=QA", "/turnover/Workflow" })
        {
            var html = await (await c.GetAsync(url)).Content.ReadAsStringAsync();
            var bad = Regex.Matches(html, @"\b(?:href|action|formaction)=""(/[^""]*)""").Select(m => m.Groups[1].Value)
                .Where(v => !v.StartsWith("/turnover/") && v != "/turnover").ToList();
            Assert.True(bad.Count == 0, $"{url}: links outside the application path: {string.Join(", ", bad.Take(5))}");
        }
        Assert.Contains("data-base=\"/turnover/\"", await (await c.GetAsync("/turnover/")).Content.ReadAsStringAsync());
    }

    private sealed class PathBaseFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => { app.UsePathBase("/turnover"); next(app); };
    }

    [Fact]
    public async Task Windows_login_without_role_groups_does_not_start()
    {
        var old = Environment.GetEnvironmentVariable("Turnover__Auth__Mode");
        Environment.SetEnvironmentVariable("Turnover__Auth__Mode", "Windows");
        Environment.SetEnvironmentVariable("Turnover__RoleGroups__Approver__0", " ");
        try
        {
            Environment.SetEnvironmentVariable("Logging__File__Path", WebFactory.LogDir);
            using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            Assert.Contains("RoleGroups", Assert.Throws<InvalidOperationException>(() => f.CreateClient()).Message);
            // The reason is also in the log folder: under IIS nobody sees the console.
            var startup = Directory.GetFiles(WebFactory.LogDir, "*-startup-*.log");
            Assert.Contains(startup, s => File.ReadAllText(s).Contains("RoleGroups"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Turnover__RoleGroups__Approver__0", null);
            Environment.SetEnvironmentVariable("Turnover__Auth__Mode", old);
        }
    }
}
