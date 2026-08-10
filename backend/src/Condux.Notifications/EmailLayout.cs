using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Condux.Core.Events;

namespace Condux.Notifications;

/// <summary>A call-to-action button in a styled email (label + absolute URL).</summary>
public sealed record EmailButton(string Label, string Url);

/// <summary>A label/value row rendered in the email's facts table (value shown in monospace). An optional
/// <see cref="ValueColorHex"/> tints the value (e.g. a red/green week-over-week trend); null keeps the
/// default text color.</summary>
public sealed record EmailFact(string Label, string Value, string? ValueColorHex = null);

/// <summary>The structured content of a styled email. The plain-text body is passed separately (it stays
/// the source of truth for the text/plain part and the other channels), so this drives only the HTML view.</summary>
public sealed record EmailContent(
    string Heading,
    IReadOnlyList<string> Paragraphs,
    IReadOnlyList<EmailFact>? Facts = null,
    EmailButton? Button = null,
    string? Badge = null,
    string? BadgeColorHex = null);

/// <summary>The email palette + fonts. A hex mirror of the LIGHT tokens in <c>packages/brand/theme.css</c>:
/// email clients cannot consume the CSS custom properties / oklch values, so this is the single server-side
/// source. The brand is monochrome-neutral (a near-black primary) plus the event-level severity colors.</summary>
internal static class EmailTheme
{
    public const string PageBg = "#f4f4f5";
    public const string CardBg = "#ffffff";
    public const string Text = "#18181b";
    public const string BodyText = "#3f3f46";
    public const string Muted = "#71717a";
    public const string FaintText = "#a1a1aa";
    public const string Border = "#e4e4e7";
    public const string FooterBg = "#fafafa";
    public const string ButtonBg = "#18181b";
    public const string ButtonText = "#ffffff";
    public const string LinkText = "#2563eb";

    // Week-over-week trend as colored text on the white card (>= 4.5:1 for the small facts value). For an
    // error monitor, up = worse (more errors) = red, down = better = green.
    public const string TrendBad = "#b91c1c";
    public const string TrendGood = "#15803d";

    public const string HeadingFont = "'Chakra Petch', 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
    public const string BodyFont = "'Segoe UI', Roboto, Helvetica, Arial, sans-serif";
    public const string MonoFont = "'Fira Code', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";

    // Severity chip color = the theme.css light event-level hex (each keeps >= 4.5:1 with white chip text).
    public static string LevelColorHex(Level level) => level switch
    {
        Level.Fatal => "#e11d48",
        Level.Error => "#dc2626",
        Level.Warning => "#b45309",
        Level.Info => "#2563eb",
        _ => "#4b5563",
    };
}

/// <summary>Renders <see cref="EmailContent"/> into a table-based, inline-styled HTML email (the layout
/// email clients actually render reliably) and builds a multipart/alternative <see cref="MailMessage"/>
/// whose text/plain part is the given plain body and text/html part is this layout.</summary>
public static class EmailLayout
{
    /// <summary>Builds the message: plain text as <see cref="MailMessage.Body"/> (the text/plain part) plus
    /// an HTML <see cref="AlternateView"/> (per the BCL multipart/alternative pattern). All interpolated
    /// content is HTML-encoded, so error-derived text (titles, culprits) can never break out of the markup.</summary>
    public static MailMessage CreateMessage(
        string from, string to, string subject, string plainTextBody, EmailContent htmlContent)
    {
        var message = new MailMessage(from, to, subject, plainTextBody);
        var html = RenderHtml(htmlContent);
        message.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));
        return message;
    }

    public static string RenderHtml(EmailContent content)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<meta name=\"color-scheme\" content=\"light\">");
        sb.Append($"<title>{Enc(content.Heading)}</title></head>");
        sb.Append($"<body style=\"margin:0;padding:0;background:{EmailTheme.PageBg};\">");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:{EmailTheme.PageBg};\">");
        sb.Append("<tr><td align=\"center\" style=\"padding:24px 12px;\">");
        sb.Append($"<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;max-width:600px;background:{EmailTheme.CardBg};border:1px solid {EmailTheme.Border};border-radius:10px;\">");

        // Header wordmark.
        sb.Append($"<tr><td style=\"padding:20px 28px;border-bottom:1px solid {EmailTheme.Border};\">");
        sb.Append($"<span style=\"font-family:{EmailTheme.HeadingFont};font-size:18px;font-weight:700;letter-spacing:1.5px;color:{EmailTheme.Text};\">Condux</span>");
        sb.Append("</td></tr>");

        // Body.
        sb.Append("<tr><td style=\"padding:28px;\">");
        if (!string.IsNullOrEmpty(content.Badge))
        {
            var color = content.BadgeColorHex ?? EmailTheme.Text;
            sb.Append("<div style=\"margin:0 0 14px;\">");
            sb.Append($"<span style=\"display:inline-block;padding:3px 10px;border-radius:9999px;font-family:{EmailTheme.BodyFont};font-size:12px;font-weight:600;color:#ffffff;background:{color};\">{Enc(content.Badge)}</span>");
            sb.Append("</div>");
        }
        sb.Append($"<h1 style=\"margin:0 0 14px;font-family:{EmailTheme.HeadingFont};font-size:20px;line-height:1.3;font-weight:700;color:{EmailTheme.Text};\">{Enc(content.Heading)}</h1>");
        foreach (var paragraph in content.Paragraphs)
        {
            sb.Append($"<p style=\"margin:0 0 14px;font-family:{EmailTheme.BodyFont};font-size:14px;line-height:1.6;color:{EmailTheme.BodyText};\">{Enc(paragraph)}</p>");
        }
        if (content.Facts is { Count: > 0 } facts)
        {
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;margin:2px 0 20px;\">");
            foreach (var fact in facts)
            {
                sb.Append("<tr>");
                sb.Append($"<td style=\"padding:5px 12px 5px 0;font-family:{EmailTheme.BodyFont};font-size:13px;color:{EmailTheme.Muted};vertical-align:top;white-space:nowrap;\">{Enc(fact.Label)}</td>");
                sb.Append($"<td style=\"padding:5px 0;font-family:{EmailTheme.MonoFont};font-size:13px;color:{fact.ValueColorHex ?? EmailTheme.Text};width:100%;\">{Enc(fact.Value)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }
        if (content.Button is { } button)
        {
            var href = Enc(button.Url);
            sb.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:6px 0 4px;\"><tr>");
            sb.Append($"<td style=\"border-radius:8px;background:{EmailTheme.ButtonBg};\">");
            sb.Append($"<a href=\"{href}\" style=\"display:inline-block;padding:12px 24px;font-family:{EmailTheme.BodyFont};font-size:14px;font-weight:600;color:{EmailTheme.ButtonText};text-decoration:none;border-radius:8px;\">{Enc(button.Label)}</a>");
            sb.Append("</td></tr></table>");
            sb.Append($"<p style=\"margin:14px 0 0;font-family:{EmailTheme.BodyFont};font-size:12px;line-height:1.5;color:{EmailTheme.Muted};\">Or paste this link into your browser:<br>");
            sb.Append($"<a href=\"{href}\" style=\"color:{EmailTheme.LinkText};word-break:break-all;\">{Enc(button.Url)}</a></p>");
        }
        sb.Append("</td></tr>");

        // Footer.
        sb.Append($"<tr><td style=\"padding:18px 28px;border-top:1px solid {EmailTheme.Border};background:{EmailTheme.FooterBg};border-radius:0 0 10px 10px;\">");
        sb.Append($"<span style=\"font-family:{EmailTheme.BodyFont};font-size:12px;color:{EmailTheme.FaintText};\">Condux, error monitoring for developers</span>");
        sb.Append("</td></tr>");

        sb.Append("</table></td></tr></table></body></html>");
        return sb.ToString();
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
