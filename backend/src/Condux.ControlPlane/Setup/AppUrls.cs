using Condux.Core.Http;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// The control-plane's reading of the deployment's own address: which environment variables state it,
/// and what an unset one means here. The resolution itself is <see cref="AppOrigins"/> in Core, because
/// the consumer worker resolves the same two variables and cannot reference this type.
///
/// Empty is this caller's answer to absence, chosen because every consumer interpolates the result
/// straight into a link. An empty base means no absolute link can be built, so a caller should skip
/// sending rather than emit a broken one.
/// </summary>
internal static class AppUrls
{
    public static string BaseUrl(IConfiguration config) =>
        AppOrigins.ResolveBaseUrl(config["CONDUX_APP_BASE_URL"], config["CONDUX_CORS_ORIGINS"])
            ?? string.Empty;
}
