using Condux.ControlPlane.Auth;
using Condux.Core.CveScanning;
using Condux.Core.SourceControl;
using Condux.GitHub;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Registers the third-party providers the control-plane talks to: the GitHub App, Google sign-in and
/// Stripe billing, each opt-in from the environment and failing fast here on a partial config rather than
/// switching off quietly, and enterprise SSO's OIDC client, which is configured per org instead.
/// </summary>
internal static class IntegrationRegistration
{
    public static IServiceCollection AddThirdPartyIntegrations(this IServiceCollection services, IConfiguration cfg)
    {
        // GitHub App (the Conductor, #61). Opt-in: enabled only when CONDUX_GITHUB_* are set; a partial
        // config fails fast here. When unset the /api/github/* routes return 404. The token minter + repo
        // client (behind the branch-picker proxy) are only registered when enabled; endpoints that need
        // them resolve lazily after checking config.Enabled.
        var githubConfig = GitHubAppConfig.FromEnv(cfg);
        services.AddSingleton(githubConfig);
        // The user-authorization leg of connect (relinking an already-installed app). Inert without the
        // client secret, so it registers unconditionally and the endpoints check the config.
        services.AddHttpClient<GitHubUserOAuth>();
        if (githubConfig.Enabled)
        {
            // GitHub is registered under both the concrete type (its GitHub-only Dependabot read) and the
            // neutral source-host seam (#145) that the fix flow + branch picker resolve: token minter + client.
            var githubTokens = new GitHubInstallationTokens(
                new HttpClient(), githubConfig.Options!, () => DateTimeOffset.UtcNow);
            services.AddSingleton(githubTokens);
            services.AddSingleton<ISourceHostTokens>(githubTokens);
            var githubRepoClient = new GitHubRepoClient(new HttpClient());
            services.AddSingleton(githubRepoClient);
            services.AddSingleton<ISourceHostClient>(githubRepoClient);
            // GitHub's CVE fast path (ADR-0022): Dependabot has already scanned, so findings are a read.
            // The neutral scanner runs a container and queries an advisory database, which is too slow for
            // a page load and belongs behind a job, so it is not the one registered here.
            services.AddSingleton<ICveScanner>(new DependabotCveScanner(githubRepoClient));
        }

        // "Sign in with Google" (OIDC, #71). Opt-in: enabled only when CONDUX_GOOGLE_* are set; a partial
        // config fails fast here. When unset the /api/auth/oauth/google/* routes 404 and the login page hides
        // the button. The typed OIDC client is inert without config, so it's always registered for injection.
        services.AddSingleton(GoogleOAuthConfig.FromEnv(cfg));
        services.AddHttpClient<GoogleOidcClient>();

        // Enterprise SSO (per-org OIDC, #72). The exchange client is always registered (inert without a
        // config; it's only reached after the per-org sso_config is found); the per-org config STORE needs
        // the secret box, so it registers with the secrets block in AddControlPlaneServices and endpoints
        // resolve it lazily.
        services.AddHttpClient<SsoOidcClient>();

        // Stripe billing (#billing). Opt-in: enabled only when CONDUX_STRIPE_* are set; a partial config
        // fails fast. When unset the /api/orgs/{id}/billing/* + /api/stripe/webhook routes 404. The typed
        // Stripe client is inert without config, so it's always registered for injection.
        services.AddSingleton(StripeConfig.FromEnv(cfg));
        services.AddHttpClient<Billing.StripeClient>();

        return services;
    }
}
