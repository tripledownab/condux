namespace Condux.ControlPlane.Setup;

/// <summary>
/// Read-only impersonation configuration (ADR-0027). Opt-in behind a single env var
/// <c>CONDUX_IMPERSONATION_SIGNING_KEY</c> (the HMAC key that signs the <c>condux_impersonation</c>
/// cookie). Deliberately its own key rather than reusing the Stripe/GitHub/secret keys, so "view as org"
/// is not silently coupled to those features being on. When unset, <see cref="Enabled"/> is false: the
/// impersonation start/stop routes 404 and the scope grant is inert (no one can be impersonated), so a
/// deployment that never sets it has the capability fully off.
/// </summary>
internal sealed record ImpersonationConfig(string? SigningKey)
{
    public bool Enabled => !string.IsNullOrEmpty(SigningKey);

    public static ImpersonationConfig FromEnv(IConfiguration cfg) =>
        new(cfg["CONDUX_IMPERSONATION_SIGNING_KEY"]);
}
