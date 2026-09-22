using Condux.Core.Auth;
using Condux.Core.Http;

namespace Condux.Core.Alerting;

/// <summary>
/// What a channel's delivery target may be.
///
/// A target is free text an org admin supplies, and the platform then either opens a connection to it or
/// hands it to an SMTP relay. Nothing said what it could hold, so three write paths each settled for
/// "not blank" and a webhook target could name any scheme and any host. Signup is open and a fresh
/// account owns the org it creates, so that gap was reachable by a stranger rather than by a customer.
///
/// The rule is keyed on the channel because the channel is what decides it: the three HTTP transports
/// need a URL the client can open, and email needs an address. A value added to
/// <see cref="NotificationChannel"/> with no arm here is refused rather than waved through, so the next
/// transport cannot inherit the gap by being forgotten. That is also why this folds the blank check and
/// the defined check the callers used to restate: one call answers the whole question, so a fourth write
/// path cannot get half of it right.
///
/// <b>This checks the shape, not the destination.</b> It does not stop an admin naming an internal host
/// and it cannot: a name resolves when the connection is opened, so an address check here loses to a
/// record that changes between the two moments. Keeping outbound requests off internal networks is an
/// egress control in the deployment. What the check buys is that the value is a URL with a scheme the
/// client speaks, so it cannot be a reference that resolves against our own host.
/// </summary>
public static class ChannelTargets
{
    /// <summary>True when <paramref name="target"/> is a usable delivery target for
    /// <paramref name="channel"/>. An undefined channel is false, so a cast from the wire needs no
    /// separate check.</summary>
    public static bool IsValid(NotificationChannel channel, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var trimmed = target.Trim();
        return channel switch
        {
            NotificationChannel.Email => Emails.IsValid(trimmed),
            NotificationChannel.Slack or NotificationChannel.Webhook or NotificationChannel.Discord =>
                HttpUrls.IsAbsoluteHttp(trimmed),
            _ => false,
        };
    }
}
