using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Condux.GitHub;

/// <summary>
/// Mints the short-lived JWT GitHub requires to authenticate <em>as the App</em> (which is then exchanged
/// for an installation token). RS256 over the app private key, with <c>iss</c> = the App's Client ID
/// (GitHub's current recommendation over the numeric App ID). Signing uses the built-in <see cref="RSA"/>
/// primitive; only the standard JWT envelope is assembled here.
/// </summary>
public static class GitHubAppJwt
{
    public static string Create(GitHubAppOptions options, DateTimeOffset now)
    {
        // 30s back to tolerate clock skew; expiry well under GitHub's 10-minute ceiling.
        var iat = now.AddSeconds(-30).ToUnixTimeSeconds();
        var exp = now.AddMinutes(9).ToUnixTimeSeconds();

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat, exp, iss = options.ClientId }));
        var signingInput = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKeyPem);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
