using Condux.ControlPlane.Setup;
using Condux.Core.Auth;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Read-only "view as org" impersonation for platform admins (ADR-0027). A signed
/// <c>condux_impersonation</c> cookie is layered on top of the admin's REAL session cookie, so
/// <c>HttpContext.User</c> always stays the admin (audit + banner need that); the cookie only adds a
/// scoped, time-boxed READ grant for one org. Two independent controls keep it read-only: (1) the
/// middleware here blocks every non-safe HTTP method while a session is active, and (2) the scope grant in
/// <see cref="OrgAuthorization"/> is handed out for GET/HEAD only. Opt-in via
/// <see cref="ImpersonationConfig"/>.
/// </summary>
internal static class Impersonation
{
    public const string Cookie = "condux_impersonation";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const string ItemsKey = "condux:impersonated_org";

    // Routes reachable even mid-impersonation, so the admin can always get out (stop the session / log
    // out) despite the read-only block on non-safe methods.
    private static readonly string[] Escapes = ["/api/admin/impersonation/stop", "/api/auth/logout"];

    /// <summary>The org being impersonated on this request, or null when not in a view-as session.</summary>
    public static long? TargetOrgId(HttpContext http) =>
        http.Items.TryGetValue(ItemsKey, out var value) && value is long orgId ? orgId : null;

    /// <summary>
    /// Mint the cookie for a validated start (called by the impersonation start endpoint). <c>Secure</c>
    /// is set for every cookie by <see cref="CookieSecurity"/>, not decided here.
    /// </summary>
    public static void SetCookie(HttpContext http, string token, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
        });

    /// <summary>Clear the cookie (stop endpoint, logout, or an invalid cookie).</summary>
    public static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });

    /// <summary>
    /// Resolve the impersonation cookie into request scope and enforce read-only. Runs after
    /// authentication (so <c>User</c> is populated) and before authorization (so the scope grant sees it).
    /// A cookie is honored only when the feature is enabled, the caller is a platform admin, and the token
    /// validates for THIS admin; otherwise it is ignored and deleted. While a session is active, any
    /// non-safe request (except the escape routes) is refused with 403 <c>impersonation_read_only</c>.
    /// </summary>
    public static IApplicationBuilder UseImpersonation(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var cookie = http.Request.Cookies[Cookie];
            if (!string.IsNullOrEmpty(cookie))
            {
                var config = http.RequestServices.GetRequiredService<ImpersonationConfig>();
                var orgId = config.Enabled && PlatformAdmin.IsPlatformAdmin(http.User)
                    ? ImpersonationToken.Validate(
                        cookie, OrgAuthorization.CurrentUserId(http.User), DateTimeOffset.UtcNow,
                        config.SigningKey!)
                    : null;

                if (orgId is { } target)
                {
                    http.Items[ItemsKey] = target;
                    if (!IsSafe(http.Request.Method) && !IsEscape(http.Request.Path))
                    {
                        http.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await http.Response.WriteAsJsonAsync(
                            new ErrorResponse("impersonation_read_only"), http.RequestAborted);
                        return;
                    }
                }
                else
                {
                    ClearCookie(http); // stale/forged/expired/not-this-admin -> drop it
                }
            }

            await next(http);
        });

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    private static bool IsEscape(PathString path) =>
        Escapes.Any(e => path.Equals(e, StringComparison.OrdinalIgnoreCase));
}
