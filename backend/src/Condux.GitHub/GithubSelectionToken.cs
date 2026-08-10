using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.GitHub;

/// <summary>One installation the user could connect: the id we would link, and the account to show them.</summary>
public sealed record GithubSelectionCandidate(
    [property: JsonPropertyName("id")] long InstallationId,
    [property: JsonPropertyName("login")] string AccountLogin);

/// <summary>The org that asked to connect, and the installations we found for the authorizing user.</summary>
public sealed record GithubSelection(
    [property: JsonPropertyName("org")] long OrgId,
    [property: JsonPropertyName("exp")] long ExpiresAtUnix,
    [property: JsonPropertyName("candidates")] IReadOnlyList<GithubSelectionCandidate> Candidates);

/// <summary>
/// Carries the candidate installations from the OAuth callback back through the browser, for the case where
/// a user can reach more than one and has to pick. Signed rather than stored: the callback is a redirect, so
/// the alternative is server-side pending state keyed on a session, which buys nothing here.
///
/// Signing is what makes the round trip safe. The installation ids come from GitHub, and the browser must
/// not be able to add to the set or move it to another org, so the choice is only honored when it appears
/// in a token we minted. Same construction as <see cref="GithubConnectState"/>, domain-separated from it.
/// </summary>
public static class GithubSelectionToken
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Create(
        long orgId, IReadOnlyList<GithubSelectionCandidate> candidates, DateTimeOffset expiresAt, string secret)
    {
        var selection = new GithubSelection(orgId, expiresAt.ToUnixTimeSeconds(), candidates);
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(selection, Json));
        return $"{payload}.{Sign(payload, secret)}";
    }

    /// <summary>The selection the token was minted for, or null if malformed, tampered, or expired.</summary>
    public static GithubSelection? Validate(string? token, DateTimeOffset now, string secret)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        var expected = Sign(parts[0], secret);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1])))
        {
            return null;
        }

        GithubSelection? selection;
        try
        {
            // The payload was covered by the signature above, so deserializing it here is safe.
            selection = JsonSerializer.Deserialize<GithubSelection>(Base64Url.DecodeFromChars(parts[0]), Json);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }

        return selection is not null && DateTimeOffset.FromUnixTimeSeconds(selection.ExpiresAtUnix) > now
            ? selection
            : null;
    }

    // Domain-separated ("select:") so a selection token can never validate as a connect state or a webhook body.
    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes("select:" + payload)));
    }
}
