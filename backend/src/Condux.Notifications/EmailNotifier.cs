using System.Net;
using System.Net.Mail;
using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>SMTP relay settings for the email notifier, sourced from the environment (no hardcoded
/// credentials). Email delivery is opt-in: it is only registered when <c>CONDUX_SMTP_HOST</c> is set.</summary>
public sealed record SmtpOptions(string Host, int Port, string From, string? User, string? Password, bool UseSsl);

/// <summary>Sends a composed <see cref="MailMessage"/>. Abstracted so the notifier is testable without a
/// real SMTP server; <see cref="SystemSmtpSender"/> is the production implementation.</summary>
public interface ISmtpSender
{
    Task SendAsync(MailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Production SMTP sender over the BCL <see cref="SmtpClient"/> (no extra dependency). Suitable
/// for a self-hostable relay; credentials come from <see cref="SmtpOptions"/>. It is a singleton, and an
/// <see cref="SmtpClient"/> refuses a send while another is in flight, so sends take turns rather than
/// the second being dropped. A turn lasts up to the client's timeout, so a slow relay delays the rest.</summary>
public sealed class SystemSmtpSender(SmtpOptions options) : ISmtpSender, IDisposable
{
    private readonly SmtpClient _client = new(options.Host, options.Port)
    {
        EnableSsl = options.UseSsl,
        Credentials = string.IsNullOrEmpty(options.User)
            ? null
            : new NetworkCredential(options.User, options.Password),
    };

    private readonly SemaphoreSlim _turn = new(1, 1);

    public async Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            await _client.SendMailAsync(message, cancellationToken);
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _turn.Dispose();
    }
}

/// <summary>Delivers an alert or an org message as a branded HTML email (with a plain-text alternative) to
/// the channel target address. The plain-text builders stay the fallback and the source for the other
/// channels; the HTML view is layered on via <see cref="EmailLayout"/>.</summary>
public sealed class EmailNotifier(ISmtpSender sender, SmtpOptions options) : INotifier
{
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task SendAsync(
        AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default)
    {
        // No escaping is applied to the values here: they reach the HTML view as the heading, a paragraph
        // or a fact, and EmailLayout HTML-encodes all three, while the plain-text alternative is plain
        // text. Both views already show a value as written.
        var body = AlertText.Message(notification, target.Template, AlertTemplate.NoEscape);
        var content = new EmailContent(
            Heading: notification.Title,
            Paragraphs: SplitParagraphs(body),
            Facts: ToFacts(notification),
            Badge: $"{AlertText.EventLabel(notification.EventType)} · {notification.Level}",
            BadgeColorHex: EmailTheme.LevelColorHex(notification.Level));
        using var message = EmailLayout.CreateMessage(
            options.From, target.Target, AlertText.Subject(notification), body, content);
        await sender.SendAsync(message, cancellationToken);
    }

    public async Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default)
    {
        var content = new EmailContent(Heading: CleanHeading(subject), Paragraphs: SplitParagraphs(body));
        using var message = EmailLayout.CreateMessage(options.From, target, subject, body, content);
        await sender.SendAsync(message, cancellationToken);
    }

    // An advisory has no issue behind it, so it carries no Issue line.
    private static IReadOnlyList<EmailFact> ToFacts(AlertNotification notification)
    {
        var facts = new List<EmailFact>
        {
            new("Culprit", notification.Culprit),
            new("Project", notification.ProjectName),
        };
        if (AlertText.IssueId(notification) is { } issueId)
        {
            facts.Add(new("Issue", issueId));
        }
        return facts;
    }

    // The layout already shows the Condux wordmark, so drop a redundant brand prefix from the heading.
    private static string CleanHeading(string subject)
    {
        foreach (var prefix in new[] { "Condux: ", "[Condux] " })
        {
            if (subject.StartsWith(prefix, StringComparison.Ordinal))
            {
                return subject[prefix.Length..];
            }
        }
        return subject;
    }

    private static IReadOnlyList<string> SplitParagraphs(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
