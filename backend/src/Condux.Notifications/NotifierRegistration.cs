using Condux.Core.Alerting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.Notifications;

/// <summary>Registers the alert notifiers (#56-58). Webhook, Slack and Discord are always available (they
/// need only a per-channel target URL). Email is opt-in: it registers only when an SMTP relay is configured
/// via <c>CONDUX_SMTP_HOST</c> (host/port/from/user/password/ssl, env-driven — no hardcoded creds).</summary>
public static class NotifierRegistration
{
    public static IServiceCollection AddAlertNotifiers(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient(WebhookNotifier.ClientName);
        services.AddHttpClient(SlackNotifier.ClientName);
        services.AddHttpClient(DiscordNotifier.ClientName);
        services.AddSingleton<INotifier, WebhookNotifier>();
        services.AddSingleton<INotifier, SlackNotifier>();
        services.AddSingleton<INotifier, DiscordNotifier>();

        var smtpHost = config["CONDUX_SMTP_HOST"];
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            var port = int.TryParse(config["CONDUX_SMTP_PORT"], out var parsed) ? parsed : 587;
            var from = config["CONDUX_SMTP_FROM"] ?? "alerts@condux.local";
            // SSL on unless explicitly disabled (a local relay like MailHog runs plaintext).
            var useSsl = !string.Equals(config["CONDUX_SMTP_SSL"], "false", StringComparison.OrdinalIgnoreCase);
            services.AddSingleton(new SmtpOptions(
                smtpHost, port, from, config["CONDUX_SMTP_USER"], config["CONDUX_SMTP_PASSWORD"], useSsl));
            services.AddSingleton<ISmtpSender, SystemSmtpSender>();
            services.AddSingleton<INotifier, EmailNotifier>();
        }

        return services;
    }
}
