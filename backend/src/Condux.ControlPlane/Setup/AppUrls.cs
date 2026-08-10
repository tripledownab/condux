namespace Condux.ControlPlane.Setup;

/// <summary>
/// Resolves the dashboard's absolute base URL for links that must work outside the browser — notably
/// invite emails, where a same-origin relative path is useless. Precedence: an explicit
/// <c>CONDUX_APP_BASE_URL</c> (set in same-origin production, e.g. https://app.condux.ai), else the first
/// <c>CONDUX_CORS_ORIGINS</c> entry (split-origin dev, e.g. http://localhost:3000), else empty. An empty
/// result means we cannot build an absolute link, so a caller should skip sending rather than emit a
/// broken one. Any trailing slash is trimmed so callers concatenate a leading-slash path cleanly.
/// </summary>
internal static class AppUrls
{
    public static string BaseUrl(IConfiguration config)
    {
        var explicitUrl = config["CONDUX_APP_BASE_URL"];
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl.TrimEnd('/');
        }

        var firstCorsOrigin = (config["CONDUX_CORS_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return firstCorsOrigin.Length > 0 ? firstCorsOrigin[0].TrimEnd('/') : string.Empty;
    }
}
