namespace Condux.ControlPlane.Setup;

/// <summary>
/// Decides once, at startup, whether this deployment's cookies carry <c>Secure</c>. The pipeline applies
/// the answer to every cookie the app writes, so a cookie added later inherits it instead of restating it.
///
/// The decision reads the deployment's own declared base URL rather than the request. TLS ends at the
/// edge proxy, so the inbound request is plain http and its scheme describes the last hop rather than
/// how the browser reached us. Three cookie writers used to ask it individually, which is both a rule
/// stated three times and a rule stated in the one place that cannot answer it. Honouring
/// <c>X-Forwarded-Proto</c> instead would move the decision to a header the client can send, where a
/// forged value downgrades the cookie and the only defence left is a known-proxy address list that has
/// to track wherever the proxy actually runs. The deployment already states its scheme
/// in one place, so ask that.
/// </summary>
internal static class CookieSecurity
{
    /// <summary>
    /// <see cref="CookieSecurePolicy.Always"/> when the dashboard is served over https, else
    /// <see cref="CookieSecurePolicy.SameAsRequest"/>. Both only ever ADD the attribute, never remove it,
    /// so plain-http dev and the in-memory TestServer keep working, and a cookie that must be Secure for
    /// its own reasons (SameSite=None on the SAML return leg) stays Secure under either.
    /// </summary>
    public static CookieSecurePolicy Policy(IConfiguration config) =>
        AppUrls.BaseUrl(config).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
}
