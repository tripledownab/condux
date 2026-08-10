using Condux.Core.WeeklySummaries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Condux.Notifications;

/// <summary>
/// Sends the weekly summary email directly to each recipient (ADR-0031), following the InviteMailer pattern:
/// best-effort and opt-in — the SMTP sender and options are resolved as zero-or-one, so the send is a no-op
/// (returns 0) when <c>CONDUX_SMTP_HOST</c> is unset. A per-recipient failure is logged, never thrown, so one
/// bad address cannot stop the rest of the org's members from receiving it.
/// </summary>
public sealed class WeeklySummaryMailer(
    IEnumerable<ISmtpSender> senders,
    IEnumerable<SmtpOptions> smtpOptions,
    IConfiguration config,
    ILogger<WeeklySummaryMailer> logger)
{
    /// <summary>Send the digest to every recipient; returns the number delivered (0 when SMTP is off).</summary>
    public async Task<int> SendAsync(
        WeeklySummary summary, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        var sender = senders.FirstOrDefault();
        var options = smtpOptions.FirstOrDefault();
        if (sender is null || options is null)
        {
            logger.LogInformation("Weekly summary skipped: SMTP is not configured (CONDUX_SMTP_HOST unset).");
            return 0;
        }

        var dashboardUrl = DashboardUrl();
        var subject = WeeklySummaryText.Subject(summary);
        var body = WeeklySummaryText.Body(summary, dashboardUrl);
        var content = WeeklySummaryEmail.Content(summary, dashboardUrl);

        var sent = 0;
        foreach (var recipient in recipients.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            try
            {
                using var message = EmailLayout.CreateMessage(options.From, recipient, subject, body, content);
                await sender.SendAsync(message, cancellationToken);
                sent++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "weekly summary email to a member failed");
            }
        }

        return sent;
    }

    // The absolute dashboard base URL for the CTA link; null in split-origin dev with nothing configured, in
    // which case the email omits the button. Mirrors AppUrls.BaseUrl (ControlPlane) — the Consumer worker
    // cannot reference that type, and the env keys are the single source of the value.
    private string? DashboardUrl() =>
        config["CONDUX_APP_BASE_URL"] is { Length: > 0 } explicitUrl
            ? explicitUrl
            : config["CONDUX_CORS_ORIGINS"]
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
}
