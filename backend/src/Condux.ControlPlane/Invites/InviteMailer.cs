using Condux.ControlPlane.Setup;
using Condux.Notifications;

namespace Condux.ControlPlane.Invites;

/// <summary>
/// Sends an org invite email (#84 follow-up). Best-effort and opt-in: email delivery relies on the same
/// SMTP relay as the alert notifiers, registered only when <c>CONDUX_SMTP_HOST</c> is set, so the sender
/// and options are resolved as zero-or-one via <see cref="IEnumerable{T}"/> rather than required. When SMTP
/// is off, or no absolute base URL is configured (so the accept link would be unusable), the send is a
/// no-op that returns false. The invite is created regardless, and the raw token is still returned to the
/// admin as an out-of-band fallback.
/// </summary>
public sealed class InviteMailer(
    IEnumerable<ISmtpSender> senders,
    IEnumerable<SmtpOptions> smtpOptions,
    IConfiguration config,
    ILogger<InviteMailer> logger)
{
    public async Task<bool> SendAsync(
        string toEmail, string orgName, string inviterEmail, string role, string rawToken,
        CancellationToken cancellationToken = default)
    {
        var sender = senders.FirstOrDefault();
        var options = smtpOptions.FirstOrDefault();
        if (sender is null || options is null)
        {
            logger.LogInformation("Invite email skipped: SMTP is not configured (CONDUX_SMTP_HOST unset).");
            return false;
        }

        var baseUrl = AppUrls.BaseUrl(config);
        if (string.IsNullOrEmpty(baseUrl))
        {
            logger.LogWarning(
                "Invite email skipped: no absolute base URL (set CONDUX_APP_BASE_URL or CONDUX_CORS_ORIGINS).");
            return false;
        }

        var acceptLink = $"{baseUrl}/invite?token={Uri.EscapeDataString(rawToken)}";
        var email = InviteEmailText.Compose(orgName, inviterEmail, role, acceptLink);
        var content = new EmailContent(
            Heading: $"You are invited to join {orgName}",
            Paragraphs:
            [
                $"{inviterEmail} invited you to join {orgName} on Condux as {role}.",
                "Sign in or create your Condux account with this email address to join.",
                $"This invitation expires in {InviteLifetime.Days} days. "
                    + "If you were not expecting it, you can ignore this email.",
            ],
            Button: new EmailButton("Accept invitation", acceptLink));
        using var message = EmailLayout.CreateMessage(options.From, toEmail, email.Subject, email.Body, content);
        await sender.SendAsync(message, cancellationToken);
        return true;
    }
}
