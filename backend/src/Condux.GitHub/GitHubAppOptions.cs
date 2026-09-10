namespace Condux.GitHub;

/// <summary>
/// GitHub App credentials, sourced from the environment (<c>CONDUX_GITHUB_*</c>). <c>PrivateKeyPem</c> is
/// the raw <c>.pem</c> contents. The GitHub integration is opt-in: when these are not configured the
/// Conductor's GitHub path stays off (like the SMTP-notifier opt-in).
/// </summary>
public sealed record GitHubAppOptions(string ClientId, string PrivateKeyPem, string WebhookSecret)
{
    /// <summary>The GitHub REST API base. Overridable for tests and GitHub Enterprise.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";
}
