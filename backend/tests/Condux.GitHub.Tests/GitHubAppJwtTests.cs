using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

public class GitHubAppJwtTests
{
    // A throwaway key generated per test run, in the PKCS#1 PEM shape GitHub hands out.
    private static (GitHubAppOptions Options, RSA Rsa) NewApp()
    {
        var rsa = RSA.Create(2048);
        var options = new GitHubAppOptions("Iv1.testclientid", rsa.ExportRSAPrivateKeyPem(), "whsec");
        return (options, rsa);
    }

    [Fact]
    public void Mints_a_jwt_whose_rs256_signature_verifies_against_the_app_key()
    {
        var (options, rsa) = NewApp();
        var jwt = GitHubAppJwt.Create(options, DateTimeOffset.UnixEpoch.AddYears(56));

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = FromBase64Url(parts[2]);
        Assert.True(rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Uses_the_client_id_as_issuer_and_a_sub_ten_minute_window()
    {
        var (options, _) = NewApp();
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var jwt = GitHubAppJwt.Create(options, now);

        using var payload = JsonDocument.Parse(FromBase64Url(jwt.Split('.')[1]));
        var root = payload.RootElement;

        Assert.Equal("Iv1.testclientid", root.GetProperty("iss").GetString());
        var iat = root.GetProperty("iat").GetInt64();
        var exp = root.GetProperty("exp").GetInt64();
        Assert.True(iat < now.ToUnixTimeSeconds()); // backdated for clock skew
        Assert.True(exp - iat <= 600); // within GitHub's 10-minute ceiling
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
