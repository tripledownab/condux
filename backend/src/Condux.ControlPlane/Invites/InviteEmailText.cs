namespace Condux.ControlPlane.Invites;

/// <summary>The composed subject and body of an invite email.</summary>
public sealed record InviteEmail(string Subject, string Body);

/// <summary>
/// Composes the plain-text invite email. Pure and dependency-free so the copy is unit-testable and stays
/// out of the delivery path. The invitee is a brand-new user (they have no account yet), so the body tells
/// them to sign in or sign up with this exact address before the accept link will let them join.
/// </summary>
public static class InviteEmailText
{
    // Mirrors the OrgInviteRepository lifetime; stated in the body so the invitee knows the window.
    public const int ExpiresInDays = 7;

    public static InviteEmail Compose(string orgName, string inviterEmail, string role, string acceptLink)
    {
        var subject = $"You are invited to join {orgName} on Condux";
        var body =
            $"{inviterEmail} invited you to join {orgName} on Condux as {role}.\n\n" +
            $"Accept the invitation:\n{acceptLink}\n\n" +
            "Sign in or create your Condux account with this email address to join. " +
            $"This invitation expires in {ExpiresInDays} days.\n\n" +
            "If you were not expecting this invitation, you can ignore this email.";
        return new InviteEmail(subject, body);
    }
}
