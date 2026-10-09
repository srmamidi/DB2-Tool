using System.Security.Claims;
using IbmTurnover.Core;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Web.Infrastructure;

public enum AuthMode { Demo, Windows, EntraId }

public static class AuthModes
{
    public static AuthMode Parse(string value) => value.Trim().ToUpperInvariant() switch
    {
        "DEMO" => AuthMode.Demo,
        "WINDOWS" => AuthMode.Windows,
        "ENTRAID" or "ENTRA" or "AZUREAD" => AuthMode.EntraId,
        _ => throw new InvalidOperationException($"Turnover:Auth:Mode must be Windows, EntraId or Demo (got '{value}')."),
    };
}

public sealed record AuthSettings(AuthMode Mode);

public sealed record RoleGroups(IReadOnlyDictionary<string, string[]> Map);

public interface ICurrentUser
{
    TurnoverUser User { get; }
    bool IsDemo { get; }
    AuthMode Mode { get; }
}

/// <summary>
/// Demo (Development only): a cookie picks one of four sample users.
/// Windows: the AD identity; roles from AD group membership (Turnover:RoleGroups).
/// EntraId: the Microsoft sign-in; roles from app roles named Developer / Approver / ReleaseManager, or from
/// group object ids listed in Turnover:RoleGroups.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor http, AuthSettings auth, RoleGroups groups) : ICurrentUser
{
    public const string Cookie = "tov-user";
    private static readonly string[] AllRoles = [Roles.Developer, Roles.Approver, Roles.ReleaseManager];

    public static readonly TurnoverUser[] DemoUsers =
    [
        new("alice", [Roles.Developer]),
        new("bob", [Roles.Approver]),
        new("carol", [Roles.Approver]),
        new("dave", [Roles.ReleaseManager]),
    ];

    public bool IsDemo => auth.Mode == AuthMode.Demo;
    public AuthMode Mode => auth.Mode;

    public TurnoverUser User
    {
        get
        {
            var ctx = http.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
            if (IsDemo)
            {
                var name = ctx.Request.Cookies[Cookie];
                return DemoUsers.FirstOrDefault(u => u.Name == name) ?? DemoUsers[0];
            }
            var p = ctx.User;
            if (p.Identity?.IsAuthenticated != true)
                throw new UnauthorizedAccessException("Not signed in.");
            var name2 = p.FindFirst("preferred_username")?.Value ?? p.Identity.Name ?? p.FindFirst(ClaimTypes.Upn)?.Value
                        ?? throw new UnauthorizedAccessException("The sign-in has no user name.");
            return new TurnoverUser(name2, RolesFor(p, groups.Map));
        }
    }

    /// <summary>App-role claims named after a Turnover role, or membership of a mapped group (Windows group or Entra group id).</summary>
    public static IReadOnlyList<string> RolesFor(ClaimsPrincipal p, IReadOnlyDictionary<string, string[]> map) =>
        AllRoles.Where(role =>
                p.IsInRole(role) || p.HasClaim("roles", role) ||
                (map.TryGetValue(role, out var gs) && gs.Any(g => !string.IsNullOrWhiteSpace(g) && (p.IsInRole(g) || p.HasClaim("groups", g)))))
            .ToList();
}
