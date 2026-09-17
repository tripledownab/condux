using System.Security.Cryptography;

namespace Condux.Core.Auth;

/// <summary>
/// The rules for proving that an org controls the email domain its SSO config claims, and for losing that
/// proof again (ADR-0043): mint the challenge, name the records that carry it, decide whether a set of TXT
/// values answers it, and say how long a claim survives once its record stops resolving.
///
/// The token is published in public DNS by design, so it is a nonce and not a credential, and it
/// deliberately does not go through <see cref="ScopedToken"/>. Storing it there would put a public string
/// in the one place this codebase reserves for machine credentials and imply a secrecy it does not have.
///
/// It still has to be random rather than derived from the domain. A derived value is the same for
/// everybody, so whoever reads it first can publish it and claim a domain they have just learned about.
/// </summary>
public static class DomainVerification
{
    private const int TokenBytes = 24;

    /// <summary>What the org publishes. Named like the record itself so an admin reading their own DNS
    /// zone can tell what put it there.</summary>
    public const string ValuePrefix = "condux-domain-verification=";

    /// <summary>The subdomain checked alongside the apex, for an org whose apex TXT records are crowded.</summary>
    public const string ChallengeLabel = "_condux-challenge";

    /// <summary>A fresh challenge for one org's claim on one domain.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The full TXT value the org publishes.</summary>
    public static string RecordValue(string token) => ValuePrefix + token;

    /// <summary>
    /// How long a proved claim keeps routing after its record stops resolving.
    ///
    /// A grace period rather than an immediate clearing, because most ways a published record stops
    /// resolving are not the org's doing: a registrar migration, a zone transfer, a resolver having a bad
    /// day. Clearing on the first failure would turn any of those into an authentication outage, and a
    /// member who only ever signed in through the IdP holds no password to fall back on.
    ///
    /// Long enough to survive a holiday weekend and a slow ticket queue. Short enough that a domain which
    /// genuinely changed hands does not keep routing for another month.
    /// </summary>
    public static readonly TimeSpan VerificationGrace = TimeSpan.FromDays(7);

    /// <summary>When a claim whose record went missing at <paramref name="lostAt"/> stops routing. The
    /// notice sent at that moment names this date, so the org reads a deadline rather than a duration it
    /// has to do arithmetic on.</summary>
    public static DateTimeOffset LapsesAt(DateTimeOffset lostAt) => lostAt + VerificationGrace;

    /// <summary>
    /// The names to query, in the order they are tried. The apex is documented and listed first because it
    /// is the form Google and Microsoft use, so it is the one an admin recognises; the challenge subdomain
    /// is offered for a zone whose apex is already crowded.
    /// </summary>
    public static IReadOnlyList<string> RecordNames(string domain) =>
        [domain, $"{ChallengeLabel}.{domain}"];

    /// <summary>
    /// Whether any of the resolved TXT values carries this claim's token.
    ///
    /// A resolver may return a value quoted, and a TXT record longer than 255 bytes arrives as several
    /// quoted strings, so each value is unquoted and rejoined before it is compared. The comparison is
    /// ordinal and not fixed-time: the token is public, so there is no secret here to leak by timing, and
    /// saying otherwise would overstate what this does.
    /// </summary>
    public static bool IsAnswered(IEnumerable<string> txtValues, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = RecordValue(token);
        return txtValues.Any(value => string.Equals(Unquote(value), expected, StringComparison.Ordinal));
    }

    private static string Unquote(string value) =>
        value.Contains('"')
            ? string.Concat(value.Split(
                '"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : value.Trim();
}

/// <summary>How a check of one domain ended. <c>RecordMissing</c> and <c>ResolverUnavailable</c> are kept
/// apart because they are different facts about different parties: the first says the org has not
/// published the record, the second says we could not find out. Collapsing them would tell an admin their
/// DNS is wrong during our own outage, and would let a resolver problem count against the org's grace
/// period as though the record had been withdrawn.</summary>
public enum DomainCheckOutcome
{
    Verified,
    RecordMissing,
    ResolverUnavailable,
}
