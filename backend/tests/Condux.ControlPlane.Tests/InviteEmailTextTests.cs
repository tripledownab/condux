using Condux.ControlPlane.Invites;
using Xunit;

namespace Condux.ControlPlane.Tests;

public class InviteEmailTextTests
{
    [Fact]
    public void Compose_states_the_org_inviter_role_and_link()
    {
        var email = InviteEmailText.Compose(
            "Acme", "boss@acme.io", "member", "https://app.condux.ai/invite?token=abc");

        Assert.Contains("Acme", email.Subject);
        Assert.Contains("boss@acme.io", email.Body);
        Assert.Contains("member", email.Body);
        Assert.Contains("https://app.condux.ai/invite?token=abc", email.Body);
        // No em-dash in copy (house style).
        Assert.DoesNotContain('—', email.Subject + email.Body);
    }
}
