using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Decides whether an org has proved control of the domain its SSO config claims (ADR-0043), and is the
/// one place the self-hosted opt-out is read.
///
/// A company running Condux for itself, on an internal domain with no public DNS, cannot publish the
/// record and gains nothing from the check, being the only tenant. <c>CONDUX_SSO_SKIP_DOMAIN_VERIFICATION</c>
/// makes the check pass for that deployment. It is deliberately environment-only and must never become a
/// per-org API field: a tenant that can switch off its own domain verification has none.
///
/// The opt-out answers the check rather than removing the step, so one invariant holds everywhere. Only a
/// verified row routes a login and only a verified row takes the domain, whether the proof came from DNS
/// or from the deployment saying it is not needed.
/// </summary>
internal sealed class SsoDomainVerifier(DohTxtResolver resolver, IConfiguration cfg)
{
    // Case-insensitive, like the SMTP SSL flag in NotifierRegistration: an operator who wrote "True" and
    // believes they switched the check off would otherwise get it left on with nothing saying so.
    private bool SkipVerification =>
        cfg["CONDUX_SSO_SKIP_DOMAIN_VERIFICATION"] is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    public Task<DomainCheckOutcome> CheckAsync(StoredSsoConfig config, CancellationToken cancellationToken) =>
        SkipVerification
            ? Task.FromResult(DomainCheckOutcome.Verified)
            : resolver.CheckAsync(config.EmailDomain, config.VerificationToken, cancellationToken);
}
