using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IbmTurnover.Tests;

public sealed class WebFactory : WebApplicationFactory<Program>
{
    // One directory for every factory in the run: the setting is a process-wide environment variable.
    public static readonly string SharedDataDir = Path.Combine(Path.GetTempPath(), "tov-web-" + Guid.NewGuid().ToString("N"));
    public string DataDir => SharedDataDir;

    public WebFactory()
    {
        // Environment variables override web.config (the same mechanism the dev launch profile uses).
        Environment.SetEnvironmentVariable("Turnover__Auth__Mode", "Demo");
        Environment.SetEnvironmentVariable("Turnover__DataDirectory", DataDir);
        Environment.SetEnvironmentVariable("Logging__File__Path", LogDir);   // test runs never write logs into the repo
    }

    public static string LogDir => Path.Combine(SharedDataDir, "logs");

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}

[Collection("web")]
public class WebTests(WebFactory factory) : IClassFixture<WebFactory>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("/")]
    [InlineData("/Compare?source=DEV&target=QA")]
    [InlineData("/Compare?mode=library&libEnv=QA&fromLib=ORDQA&toLib=ORDQA2")]
    [InlineData("/Packages")]
    [InlineData("/Environments")]
    [InlineData("/LibraryMove?env=QA&from=CUSQA&to=CUSQA2")]
    [InlineData("/Compare?source=DEV&target=QA&view=report")]
    [InlineData("/Compare?mode=library&libEnv=DEV&fromLib=SMAMIDI&toLib=ORDDEV&sourceOnly=true")]
    [InlineData("/Snapshots?env=QA")]
    [InlineData("/Workflow?env=DEV")]
    [InlineData("/Checkouts")]
    [InlineData("/Checkouts?history=true")]
    [InlineData("/Source")]
    public async Task Pages_render_and_every_post_form_carries_the_antiforgery_token(string url)
    {
        var res = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        foreach (Match form in Regex.Matches(html, @"<form[^>]*method=""post""[\s\S]*?</form>", RegexOptions.IgnoreCase))
            Assert.Contains("__RequestVerificationToken", form.Value);
    }

    [Fact]
    public async Task Drift_endpoint_matches_the_compare_page_counts()
    {
        var client = Client();
        var json = await client.GetStringAsync("/Home/Drift?source=DEV&target=QA");
        var page = await client.GetStringAsync("/Compare?source=DEV&target=QA");
        var changes = Regex.Match(json, @"""changes"":(\d+)").Groups[1].Value;
        var blockers = Regex.Match(json, @"""blockers"":(\d+)").Groups[1].Value;
        Assert.Contains($"<strong>{changes}</strong><span>differences</span>", page);
        Assert.Contains($"<strong>{blockers}</strong><span>blockers</span>", page);
    }

    [Fact]
    public async Task A_package_can_be_created_over_http_with_the_token()
    {
        var client = Client();
        var page = await client.GetStringAsync("/Compare?source=QA&target=STAGE");
        var form = Regex.Match(page, @"<form[^>]*id=""pkg-form""[\s\S]*?</form>").Value;
        var token = Regex.Match(form, @"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""").Groups[1].Value;
        var selected = Regex.Matches(form, @"name=""selected"" value=""([^""]+)"" checked=""checked""").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(token);
        Assert.NotEmpty(selected);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("mode", "promote"), new("source", "QA"), new("target", "STAGE"),
            new("title", "Web test package"), new("ticket", "CHG-1"),
        };
        fields.AddRange(selected.Select(s => new KeyValuePair<string, string>("selected", s)));
        var post = await client.PostAsync("/Compare/CreatePackage", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Matches(@"/Packages/Details/\d+", post.Headers.Location!.ToString());

        var withoutToken = await client.PostAsync("/Compare/CreatePackage", new FormUrlEncodedContent(fields.Skip(1)));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
    }
}

[Collection("web")]
public class WebEndpointTests(WebFactory factory) : IClassFixture<WebFactory>
{
    [Fact]
    public async Task Csv_export_and_lazy_side_by_side_work()
    {
        var client = factory.CreateClient();
        var csv = await client.GetAsync("/Compare/Csv?mode=promote&source=DEV&target=QA");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("After promotion", await csv.Content.ReadAsStringAsync());

        var id = Uri.EscapeDataString("COLUMN:ORDERS:ORDER_HEADER.TOTAL");
        var part = await client.GetStringAsync($"/Compare/Diff?mode=promote&source=DEV&target=QA&id={id}");
        Assert.Contains("DECIMAL(9,2)", part);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Compare/Diff?mode=promote&source=DEV&target=QA&id=NOPE")).StatusCode);
    }

    [Fact]
    public async Task Snapshot_capture_then_history_lists_it()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await client.GetStringAsync("/Snapshots?env=STAGE");
        var token = Regex.Match(page, @"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""").Groups[1].Value;
        var post = await client.PostAsync("/Snapshots/Capture", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("env", "STAGE"), new("label", "before CHG-1")]));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("before CHG-1", await client.GetStringAsync("/Snapshots?env=STAGE"));
    }

    [Fact]
    public async Task Workflow_shows_every_catalog_check_after_a_read()
    {
        var client = factory.CreateClient();
        await client.GetStringAsync("/Compare?source=DEV&target=QA");
        var html = await client.GetStringAsync("/Workflow?env=DEV");
        Assert.Contains("all checks passed", html);
        Assert.Contains("Sample catalog", html);
    }
}

/// <summary>Web tests share process-wide environment variables, so they never run in parallel with each other.</summary>
[CollectionDefinition("web", DisableParallelization = true)]
public sealed class WebCollection;

[Collection("web")]
public class HostConfigurationTests
{
    [Fact]
    public void Web_config_app_settings_and_connection_strings_are_read()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var file = Path.Combine(dir, "web.config");
        File.WriteAllText(file, """
            <configuration>
              <appSettings><add key="Turnover:Mode" value="Live" /><add key="Turnover__Catalog__CacheMinutes" value="3" /></appSettings>
              <connectionStrings><add name="Db2Qa" connectionString="Driver={IBM i Access ODBC Driver};System=QA;" /></connectionStrings>
            </configuration>
            """);
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Add(new IbmTurnover.Web.Infrastructure.WebConfigSource(file, false)).Build();
        Assert.Equal("Live", cfg["Turnover:Mode"]);
        Assert.Equal("3", cfg["Turnover:Catalog:CacheMinutes"]);
        Assert.StartsWith("Driver={IBM i Access ODBC Driver}", Microsoft.Extensions.Configuration.ConfigurationExtensions.GetConnectionString(cfg, "Db2Qa"));
    }

    [Theory]
    [InlineData(null, "KeyVault:Uri")]
    [InlineData("http://plain.example", "KeyVault:Uri")]
    [InlineData("https://vault.vault.azure.net/", "TenantId")]
    public void Key_vault_enabled_but_misconfigured_stops_startup(string? uri, string message)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { ContentRootPath = Path.GetTempPath() });
        builder.Configuration["WebConfigPath"] = Path.Combine(Path.GetTempPath(), "no-such-web.config");
        builder.Configuration["KeyVault:Enabled"] = "true";
        builder.Configuration["KeyVault:Uri"] = uri;
        var ex = Assert.Throws<InvalidOperationException>(() => IbmTurnover.Web.Infrastructure.HostConfiguration.Configure(builder));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Key_vault_secret_names_map_to_configuration_keys()
    {
        var m = new IbmTurnover.Web.Infrastructure.PrefixSecretManager("Turnover-");
        var secret = Azure.Security.KeyVault.Secrets.SecretModelFactory.KeyVaultSecret(
            Azure.Security.KeyVault.Secrets.SecretModelFactory.SecretProperties(name: "Turnover-ConnectionStrings--Db2Prod"), "x");
        Assert.Equal("ConnectionStrings:Db2Prod", m.GetKey(secret));
        Assert.True(m.Load(secret.Properties));
        Assert.False(m.Load(Azure.Security.KeyVault.Secrets.SecretModelFactory.SecretProperties(name: "Other-Secret")));
    }

    [Fact]
    public void Demo_login_is_refused_outside_development()
    {
        using var prod = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var old = Environment.GetEnvironmentVariable("Turnover__Auth__Mode");
        Environment.SetEnvironmentVariable("Turnover__Auth__Mode", "Demo");
        try { Assert.Contains("Development", Assert.Throws<InvalidOperationException>(() => prod.CreateClient()).Message); }
        finally { Environment.SetEnvironmentVariable("Turnover__Auth__Mode", old); }
    }
}
