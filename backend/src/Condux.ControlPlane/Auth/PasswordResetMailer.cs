using Condux.ControlPlane.Setup;
using Condux.Notifications;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Sends the password reset email. Best-effort and opt-in on the same SMTP relay as the alert notifiers
/// and invites, so the sender is resolved as zero-or-one rather than required.
///
/// Returning false when SMTP is off is deliberate and is NOT reported to the caller of the API: telling
/// a stranger "we could not send that" still tells them the address exists. An operator sees it in the
/// log instead, which is the only place the difference is safe to state.
/// </summary>
public sealed class PasswordResetMailer(
    IEnumerable<ISmtpSender> senders,
    IEnumerable<SmtpOptions> smtpOptions,
    IConfiguration config,
    ILogger<PasswordResetMailer> logger)
{
    public async Task<bool> SendAsync(
        string toEmail, string rawToken, CancellationToken cancellationToken = default)
    {
        var sender = senders.FirstOrDefault();
        var options = smtpOptions.FirstOrDefault();
        if (sender is null || options is null)
        {
            logger.LogWarning(
                "Password reset email skipped: SMTP is not configured (CONDUX_SMTP_HOST unset). "
                + "Without it there is no way for a user to recover a forgotten password.");
            return false;
        }

        var baseUrl = AppUrls.BaseUrl(config);
        if (string.IsNullOrEmpty(baseUrl))
        {
            logger.LogWarning(
                "Password reset email skipped: no absolute base URL "
                + "(set CONDUX_APP_BASE_URL or CONDUX_CORS_ORIGINS).");
            return false;
        }

        var resetLink = $"{baseUrl}/reset?token={Uri.EscapeDataString(rawToken)}";
        var email = PasswordResetEmailText.Compose(resetLink);
        var content = new EmailContent(
            Heading: "Reset your Condux password",
            Paragraphs:
            [
                "Someone asked to reset the password for this Condux account.",
                $"The link works once and expires in {(int)PasswordResetEmailText.Lifetime.TotalMinutes} "
                    + "minutes.",
                "If you did not ask for this, you can ignore this email. Your password has not changed.",
            ],
            Button: new EmailButton("Set a new password", resetLink));
        using var message = EmailLayout.CreateMessage(
            options.From, toEmail, email.Subject, email.Body, content);
        await sender.SendAsync(message, cancellationToken);
        return true;
    }
}
