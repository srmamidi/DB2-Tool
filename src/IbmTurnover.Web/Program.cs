using IbmTurnover.Core;
using IbmTurnover.Core.Workflow;
using IbmTurnover.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// web.config <appSettings>/<connectionStrings>, then (optionally) Azure Key Vault on top.
HostConfiguration.Configure(builder);

// Login: Windows (IIS / Negotiate), EntraId (Microsoft sign-in page) or Demo (Development only).
var authMode = AuthModes.Parse(builder.Configuration["Turnover:Auth:Mode"] ?? (builder.Configuration.GetValue<bool>("Turnover:DemoUsers") ? "Demo" : "Windows"));
builder.Configuration["Turnover:DemoUsers"] = (authMode == AuthMode.Demo).ToString();
var roleGroups = builder.Configuration.GetSection("Turnover:RoleGroups").Get<Dictionary<string, string[]>>() ?? new();
if (authMode == AuthMode.Demo && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Turnover:Auth:Mode=Demo lets anyone pick any role. It is only allowed in the Development environment.");
if (authMode == AuthMode.Windows && !new[] { "Developer", "Approver", "ReleaseManager" }.All(r => roleGroups.TryGetValue(r, out var g) && g.Any(x => !string.IsNullOrWhiteSpace(x))))
    throw new InvalidOperationException("Windows login: Turnover:RoleGroups must map Developer, Approver and ReleaseManager to at least one AD group each.");
if (authMode == AuthMode.EntraId && (string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:TenantId"]) || string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"])))
    throw new InvalidOperationException("Entra ID login: AzureAd:TenantId and AzureAd:ClientId are required.");

// Validates the environment/library map and connection strings now; a bad config never starts.
builder.Services.AddIbmTurnover(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new RoleGroups(roleGroups));
builder.Services.AddSingleton(new AuthSettings(authMode));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddHostedService<AutoSnapshotService>();
builder.Services.AddHostedService<ScheduledDeployService>();

switch (authMode)
{
    case AuthMode.Windows:
        // Inside IIS, IIS performs Windows authentication (web.config windowsAuthentication) and hands the user over;
        // the Negotiate handler is for Kestrel / HTTP.sys (local runs and self-hosting).
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_POOL_ID")))
            builder.Services.AddAuthentication(Microsoft.AspNetCore.Server.IISIntegration.IISDefaults.AuthenticationScheme);
        else
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
        break;
    case AuthMode.EntraId:
        builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
        builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.AccessDeniedPath = "/Account/AccessDenied";
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax; // the sign-in callback is a cross-site POST from login.microsoftonline.com
            });
        break;
}
if (authMode != AuthMode.Demo)
    builder.Services.AddAuthorization(o =>
    {
        // Signed in is not enough: a user needs at least one Turnover role to see anything (fail closed).
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
            .RequireAssertion(ctx => CurrentUser.RolesFor(ctx.User, roleGroups).Count > 0).Build();
    });
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();

// Subscribe snapshot history to catalog reads from the first request on.
app.Services.GetRequiredService<SnapshotService>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    await next();
});
app.UseStatusCodePagesWithReExecute("/Account/Status", "?code={0}");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
if (authMode != AuthMode.Demo)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();

public partial class Program;
