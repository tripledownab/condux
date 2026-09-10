namespace Condux.Core.Http;

/// <summary>
/// The addresses this deployment answers on, read from the two environment values that state them:
/// <c>CONDUX_CORS_ORIGINS</c> (a comma list) and <c>CONDUX_APP_BASE_URL</c> (one absolute URL).
///
/// <para>Four callers parsed that comma list with the same expression and then applied three different
/// policies to it: take the first with the explicit URL preferred, take the first with no fallback, or
/// take all of them. One mechanism, three rules, so the mechanism lives here and each caller states its
/// rule in a line. Absence belongs to the rule rather than the parse, which is why nothing here
/// substitutes a default: the control-plane wants an empty string it can concatenate, and the
/// weekly-summary mailer wants a null so it can omit its button.</para>
///
/// <para>The trimming in <see cref="ResolveBaseUrl"/> is load-bearing rather than tidiness. A surviving
/// trailing slash turns every concatenated leading-slash path into a double slash. Surrounding
/// whitespace makes the value fail a <c>https://</c> test while still reading correctly to a human, and
/// the control-plane reads that scheme to decide whether its cookies carry <c>Secure</c>.</para>
/// </summary>
public static class AppOrigins
{
    /// <summary>
    /// The configured origins, IN ORDER. Empty when none are set.
    ///
    /// <para><see cref="Condux.Core.Auth.EmailAllowlist"/> opens with the same BCL call and is not the
    /// same rule, so do not fold the two together: it drops order into a hash set, and order is the
    /// whole answer here, since the FIRST origin is the one a split-origin deployment lives at.</para>
    /// </summary>
    public static string[] Parse(string? corsOrigins) =>
        (corsOrigins ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The dashboard's absolute base URL: the explicit value when set, else the first configured origin
    /// (split-origin dev), else null. Null means no absolute link can be built, so a caller should skip
    /// rather than emit a broken one, and a value that trims away to nothing counts as absent for the
    /// same reason.
    /// </summary>
    public static string? ResolveBaseUrl(string? explicitUrl, string? corsOrigins)
    {
        var configured = string.IsNullOrWhiteSpace(explicitUrl)
            ? Parse(corsOrigins).FirstOrDefault()
            : explicitUrl;

        var trimmed = configured?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
