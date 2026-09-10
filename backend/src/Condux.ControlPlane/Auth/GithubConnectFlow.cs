using Condux.ControlPlane.Setup;
using Condux.GitHub;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// The browser half of connecting an org to a GitHub App installation: mint the signed connect state,
/// remember it in a cookie, and require the two to agree when GitHub sends the browser back. The state on
/// its own only proves which org it was minted for, and any org admin can mint one for their own org, so
/// without the cookie a state pasted into a link would let a victim's own authorization finish someone
/// else's connect. Double-submit, the same defense <see cref="OidcFlow"/> gives Google sign-in and SSO.
/// </summary>
internal static class GithubConnectFlow
{
    private const string StateCookie = "condux_github_state";

    // Long enough to install the app and choose repositories, short enough to limit replay. The cookie
    // carries the same lifetime, so the browser's half never expires before the state it guards.
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Mint a state for this org and remember it in the browser, returning the state to hand to GitHub.
    /// Called again at each hop, which refreshes both halves together so a leg never inherits the previous
    /// one's remaining time. <paramref name="installed"/> is true only on the hop that follows GitHub's
    /// install screen, and every caller states it rather than inheriting a default, because the callback
    /// reads it to tell a pending approval from a missing install.
    /// </summary>
    public static string Start(
        HttpContext http, long orgId, string returnPath, GitHubAppConfig config, bool installed)
    {
        var state = GithubConnectState.Create(
            orgId, returnPath, installed, DateTimeOffset.UtcNow.Add(StateLifetime),
            config.Options!.WebhookSecret);
        http.Response.Cookies.Append(
            StateCookie, state, OidcFlow.StateCookieOptions(lifetime: StateLifetime));
        return state;
    }

    /// <summary>
    /// What this leg is authorized to act for, or null when the state is missing, tampered with, expired,
    /// or was not the one this browser started with. The cookie is consumed either
    /// way, so a browser that has come back once cannot come back again on the same state. That is the
    /// limit of what double-submit proves: it binds the return to a browser holding the cookie, not to the
    /// user or the session, exactly as the Google and SSO flows do.
    /// </summary>
    public static GithubConnectState.Verified? Verify(HttpContext http, GitHubAppConfig config)
    {
        var returned = http.Request.Query["state"].ToString();
        var remembered = http.Request.Cookies[StateCookie];
        http.Response.Cookies.Delete(StateCookie);
        return !string.IsNullOrEmpty(remembered) && OidcFlow.FixedTimeEquals(remembered, returned)
            ? GithubConnectState.Validate(returned, DateTimeOffset.UtcNow, config.Options!.WebhookSecret)
            : null;
    }
}
