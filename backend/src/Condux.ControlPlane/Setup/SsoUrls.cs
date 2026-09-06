namespace Condux.ControlPlane.Setup;

/// <summary>
/// The three addresses an org registers in its identity provider: the OIDC redirect URI, and the SAML
/// SP entity ID and assertion consumer service URL. Built once, here, from <see cref="AppUrls"/>.
///
/// One home because two of them are load-bearing on both sides. The redirect URI goes out in the
/// authorization request and again in the token exchange, and the entity ID is the audience the IdP's
/// assertion is checked against, so a value the dashboard shows that differs from the value the server
/// sends is a failure the admin cannot diagnose: they registered exactly what the product told them to.
/// The settings tab used to rebuild all three from the browser's origin, which agrees with the server
/// only while the dashboard is served from the configured base URL.
/// </summary>
internal static class SsoUrls
{
    public static string RedirectUri(IConfiguration config) => $"{AppUrls.BaseUrl(config)}/api/auth/sso/callback";

    public static string SamlEntityId(IConfiguration config) => $"{AppUrls.BaseUrl(config)}/api/auth/sso/saml";

    public static string SamlAcsUrl(IConfiguration config) => $"{AppUrls.BaseUrl(config)}/api/auth/sso/saml/acs";
}
