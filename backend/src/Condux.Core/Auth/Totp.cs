using System.Security.Cryptography;
using System.Text;
using Petrsnd.OtpCore;
using OtpCoreTotp = Petrsnd.OtpCore.Totp;

namespace Condux.Core.Auth;

/// <summary>
/// Time-based one-time passwords, RFC 6238 over RFC 4226. HMAC-SHA1, 30-second steps, six digits,
/// which is what an authenticator app assumes when the otpauth URI omits the parameters.
/// </summary>
/// <remarks>
/// <para>
/// The code arithmetic and the base32 codec come from <c>OtpCore</c> (MIT, petrsnd/OtpCore), not from
/// us, per ADR-0008. What stays here is the policy around it, which is where the security decisions
/// live and where a library has no opinion: how much clock drift to accept, comparing in fixed time,
/// and reporting which step matched so the caller can refuse to accept it twice.
/// </para>
/// <para>
/// <c>TotpTests</c> runs RFC 6238's own published vectors through this, so the dependency is checked
/// against the specification rather than taken on trust, and a version bump that changed behaviour
/// fails the build rather than shipping.
/// </para>
/// </remarks>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int DefaultDigits = 6;
    private const OtpHmacAlgorithm Algorithm = OtpHmacAlgorithm.HmacSha1;

    /// <summary>A new random secret, base32-encoded. 20 bytes is the RFC 4226 recommended length.</summary>
    public static string NewSecret() =>
        Utilities.Base32Encode(RandomNumberGenerator.GetBytes(20), includePadding: false);

    /// <summary>The time step an instant falls in. Exposed so callers can store it as a replay guard.</summary>
    public static long StepAt(DateTimeOffset when) => when.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for one specific step. Deterministic, which is what makes it testable.</summary>
    public static string Compute(string base32Secret, long step, int digits = DefaultDigits) =>
        OtpCoreTotp.GetTotpCode(
            Utilities.Base32Decode(base32Secret), step * StepSeconds, StepSeconds, Algorithm, digits);

    /// <summary>
    /// Whether <paramref name="code"/> is valid at <paramref name="now"/>, and which step it matched.
    /// The matched step is returned so the caller can refuse to accept the same code a second time.
    /// </summary>
    /// <param name="skewSteps">
    /// Steps either side of now that are also accepted, for clock drift between the phone and us. One
    /// step is the usual choice: it tolerates 30 seconds of drift while only tripling the guess space,
    /// which rate limiting handles. A larger window widens the period an observed code stays usable in.
    /// </param>
    public static bool TryValidate(
        string base32Secret, string? code, DateTimeOffset now, out long matchedStep,
        int skewSteps = 1, int digits = DefaultDigits)
    {
        matchedStep = 0;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        code = code.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (code.Length != digits || !code.All(char.IsAsciiDigit))
        {
            return false;
        }

        var current = StepAt(now);
        for (var offset = -skewSteps; offset <= skewSteps; offset++)
        {
            var step = current + offset;
            // Fixed-time comparison: a timing oracle on the expected code would let an attacker recover
            // it digit by digit rather than having to guess the whole value.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Compute(base32Secret, step, digits)),
                    Encoding.ASCII.GetBytes(code)))
            {
                matchedStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>The otpauth URI an authenticator app scans. Label and issuer are percent-encoded.</summary>
    public static string ProvisioningUri(string base32Secret, string account, string issuer)
    {
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}"
             + $"&algorithm=SHA1&digits={DefaultDigits}&period={StepSeconds}";
    }
}
