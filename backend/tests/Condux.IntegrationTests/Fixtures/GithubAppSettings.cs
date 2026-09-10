using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// A complete GitHub App configuration for a test host. One home for it because the config is all-or-
/// nothing: a host that sets some of the values fails to start, and four test classes each carrying their
/// own copy of the list is how three of them came to be missing the user-authorization pair when it
/// stopped being optional. Add a value here, not in a test.
/// </summary>
public static class GithubAppSettings
{
    public const string WebhookSecret = "test-webhook-secret";
    public const string Slug = "condux-test";
    public const string RedirectUri = "https://app.condux.test/api/github/oauth/callback";

    /// <summary>Generated once per run: no test asserts on the key, only that a real one parses.</summary>
    private static readonly string PrivateKeyPem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    public static void Apply(IWebHostBuilder builder)
    {
        builder.UseSetting("CONDUX_GITHUB_CLIENT_ID", "Iv1.test");
        builder.UseSetting("CONDUX_GITHUB_WEBHOOK_SECRET", WebhookSecret);
        builder.UseSetting("CONDUX_GITHUB_APP_SLUG", Slug);
        builder.UseSetting("CONDUX_GITHUB_PRIVATE_KEY", PrivateKeyPem);
        builder.UseSetting("CONDUX_GITHUB_CLIENT_SECRET", "client-secret");
        builder.UseSetting("CONDUX_GITHUB_OAUTH_REDIRECT_URI", RedirectUri);
    }
}
