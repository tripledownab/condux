using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using Condux.ControlPlane.Setup;
using Condux.Storage.Postgres;
using ITfoxtec.Identity.Saml2;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// The SAML side of enterprise SSO (ADR-0032 slice 2), built on ITfoxtec.Identity.Saml2 — the vetted
/// library owns the protocol security (XML signature validation, audience + conditions checks), we only
/// assemble its per-org configuration from the stored <see cref="StoredSsoConfig"/>. The IdP's signing
/// certificate is pinned per org (uploaded by the admin, usually self-signed), so chain and revocation
/// checks are off — trust is the pinned certificate itself, the standard SAML model.
/// </summary>
internal static class SamlSso
{
    // Our SP entity ID — a stable URI the org's admin registers in their IdP (the settings tab shows it,
    // computed client-side like the OIDC redirect URI).
    private static string EntityId(IConfiguration cfg) => $"{AppUrls.BaseUrl(cfg)}/api/auth/sso/saml";

    public static Saml2Configuration Configuration(StoredSsoConfig config, IConfiguration cfg)
    {
        var saml = new Saml2Configuration
        {
            Issuer = EntityId(cfg),
            AllowedIssuer = config.Issuer,
            SingleSignOnDestination = new Uri(config.SamlSsoUrl!),
            CertificateValidationMode = X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        saml.SignatureValidationCertificates.Add(TryLoadCertificate(config.SamlCertificate!)
            ?? throw new InvalidOperationException("stored SAML certificate no longer parses"));
        saml.AllowedAudienceUris.Add(EntityId(cfg));
        return saml;
    }

    /// <summary>Parse an admin-supplied IdP signing certificate — PEM, or bare base64 DER (the form IdPs
    /// like Entra and Keycloak export as "certificate" text). Null when it isn't one.</summary>
    public static X509Certificate2? TryLoadCertificate(string value)
    {
        try
        {
            if (value.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(value);
            }
            var der = Convert.FromBase64String(string.Concat(
                value.Where(c => !char.IsWhiteSpace(c))));
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    // Where IdPs put the email, in preference order: the standard WS-Fed/OASIS claim (Entra, AD FS, Okta),
    // the LDAP mail OID, then plain attribute names (Keycloak defaults). The NameID is the fallback when
    // it is itself an email address.
    private static readonly string[] EmailClaimTypes =
    [
        ClaimTypes.Email,
        "urn:oid:0.9.2342.19200300.100.1.3",
        "email",
        "mail",
    ];

    /// <summary>The user's email from the validated assertion's claims, or null when none is present.</summary>
    public static string? Email(ClaimsIdentity identity)
    {
        foreach (var type in EmailClaimTypes)
        {
            if (identity.FindFirst(type)?.Value is { } value && Core.Auth.Emails.IsValid(value))
            {
                return value;
            }
        }
        var nameId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null && Core.Auth.Emails.IsValid(nameId) ? nameId : null;
    }
}
