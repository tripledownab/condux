using System.Security.Cryptography;
using System.Text;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

public class GitHubWebhookTests
{
    private const string Secret = "shhh-webhook-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"action":"created"}""");

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body));
    }

    [Fact]
    public void Accepts_a_correctly_signed_payload()
    {
        Assert.True(GitHubWebhook.IsValidSignature(Secret, Body, Sign(Secret, Body)));
    }

    [Fact]
    public void Rejects_a_tampered_body_wrong_secret_or_missing_header()
    {
        var valid = Sign(Secret, Body);

        Assert.False(GitHubWebhook.IsValidSignature(Secret, Encoding.UTF8.GetBytes("""{"action":"deleted"}"""), valid));
        Assert.False(GitHubWebhook.IsValidSignature("wrong-secret", Body, valid));
        Assert.False(GitHubWebhook.IsValidSignature(Secret, Body, null));
        Assert.False(GitHubWebhook.IsValidSignature(Secret, Body, "")); // empty
        Assert.False(GitHubWebhook.IsValidSignature(Secret, Body, Convert.ToHexStringLower([1, 2, 3]))); // no sha256= prefix
    }
}
