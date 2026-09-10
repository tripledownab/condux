namespace Condux.ControlPlane.Auth;

/// <summary>The composed subject and body of a password reset email.</summary>
public sealed record PasswordResetEmail(string Subject, string Body);

/// <summary>
/// Composes the plain-text password reset email. Pure and dependency-free so the copy is unit-testable
/// and stays out of the delivery path, matching how invite copy is handled.
///
/// The copy has one job beyond carrying the link: tell someone who did NOT ask what to do. A reset mail
/// is what an attacker who knows an address can cause to be sent, so the recipient has to be able to
/// read it, conclude nothing happened, and stop. Saying the old password still works is the part that
/// makes ignoring it a safe choice rather than a guess.
/// </summary>
public static class PasswordResetEmailText
{
    /// <summary>Short on purpose. It is long enough to read a mail and act, and no longer.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static PasswordResetEmail Compose(string resetLink)
    {
        const string subject = "Reset your Condux password";
        var body =
            "Someone asked to reset the password for this Condux account.\n\n" +
            $"Set a new password:\n{resetLink}\n\n" +
            $"The link works once and expires in {(int)Lifetime.TotalMinutes} minutes.\n\n" +
            "If you did not ask for this, you can ignore this email. Your password has not changed and " +
            "this link cannot be used by anyone who does not receive it.\n";
        return new PasswordResetEmail(subject, body);
    }
}
