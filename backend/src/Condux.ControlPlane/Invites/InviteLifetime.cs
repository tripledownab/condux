namespace Condux.ControlPlane.Invites;

/// <summary>
/// How long an org invite stays redeemable. One home, because the number is both enforced and stated:
/// the create endpoint writes <c>expires_at</c> from it, and the invite email tells the invitee the
/// window. It used to be two independent sevens, one in each of those places, so changing the enforced
/// one would have left the email telling every invitee a date the token no longer honoured.
/// </summary>
public static class InviteLifetime
{
    public const int Days = 7;

    /// <summary>The same window as a <see cref="TimeSpan"/>, for the endpoint that stamps expires_at.
    /// Named to match <c>JobLease.Duration</c> rather than invented here.</summary>
    public static TimeSpan Duration => TimeSpan.FromDays(Days);
}
