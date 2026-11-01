using Condux.ControlPlane.Setup;
using Condux.Core.Auth;

namespace Condux.ControlPlane.Auth;

/// <summary>One "Connect" flow's fixed names: the state's signing purpose, the cookie holding the browser's
/// half and the query key the dashboard reads the outcome from.</summary>
internal sealed record ConnectFlowKind(string Purpose, string StateCookie, string OutcomeKey);

/// <summary>
/// The browser half of connecting an org to a third party (a GitHub App installation, a Linear workspace):
/// mint the signed <see cref="ConnectState"/>, remember it in a cookie, and require the two to agree when
/// the third party sends the browser back. The state on its own only proves which org it was minted for,
/// and any org admin can mint one for their own org, so without the cookie a state pasted into a link would
/// let a victim's own authorization finish someone else's connect. Double submit, the same defence
/// <see cref="OidcFlow"/> gives Google sign in and SSO.
/// </summary>
internal static class ConnectFlow
{
    public static readonly ConnectFlowKind GitHub = new(ConnectState.GitHub, "condux_github_state", "github");
    public static readonly ConnectFlowKind Linear = new(ConnectState.Linear, "condux_linear_state", "linear");

    // Long enough to install an app and choose what it may reach, short enough to limit replay. The cookie
    // carries the same lifetime, so the browser's half never expires before the state it guards.
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Mint a state for this org and remember it in the browser, returning the state to hand over. Called
    /// again at each hop, which refreshes both halves together so a leg never inherits the previous one's
    /// remaining time. <paramref name="installed"/> is true only on GitHub's hop that follows its install
    /// screen, and every caller states it rather than inheriting a default, because GitHub's callback reads
    /// it to tell a pending approval from a missing install.
    /// </summary>
    public static string Start(
        HttpContext http, ConnectFlowKind kind, string secret, long orgId, string returnPath, bool installed)
    {
        var state = ConnectState.Create(
            kind.Purpose, orgId, returnPath, installed, DateTimeOffset.UtcNow.Add(StateLifetime), secret);
        http.Response.Cookies.Append(
            kind.StateCookie, state, OidcFlow.StateCookieOptions(lifetime: StateLifetime));
        return state;
    }

    /// <summary>
    /// What this leg is authorized to act for, or null when the state is missing, tampered with, expired, or
    /// was not the one this browser started with. The cookie is consumed either way, so a browser that has
    /// come back once cannot come back again on the same state. That is the limit of what double submit
    /// proves: it binds the return to a browser holding the cookie, not to the user or the session, exactly
    /// as the Google and SSO flows do.
    /// </summary>
    public static ConnectState.Verified? Verify(HttpContext http, ConnectFlowKind kind, string secret)
    {
        var returned = http.Request.Query["state"].ToString();
        var remembered = http.Request.Cookies[kind.StateCookie];
        http.Response.Cookies.Delete(kind.StateCookie);
        return !string.IsNullOrEmpty(remembered) && OidcFlow.FixedTimeEquals(remembered, returned)
            ? ConnectState.Validate(kind.Purpose, returned, DateTimeOffset.UtcNow, secret)
            : null;
    }

    /// <summary>
    /// Where to send the browser after a connect leg: the dashboard's base URL plus the in app path the flow
    /// started from, flagged with the outcome so the page can report it.
    /// </summary>
    public static string DashboardUrl(IConfiguration cfg, ConnectFlowKind kind, string returnPath, string outcome)
    {
        var separator = returnPath.Contains('?') ? "&" : "?";
        return $"{AppUrls.BaseUrl(cfg)}{returnPath}{separator}{kind.OutcomeKey}={outcome}";
    }

    /// <summary>
    /// Only a same origin relative path may be echoed back through the connect state (an open redirect
    /// guard): it must be a single leading slash, no protocol relative "//" and no backslash. Anything else
    /// falls back to home.
    /// </summary>
    public static string SafeReturnPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 512 && path[0] == '/'
        && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\')
            ? path
            : "/";
}
