using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

public class GithubConnectStateTests
{
    private const string Secret = "state-signing-secret";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Round_trips_the_org_id_return_path_and_installed_flag(bool installed)
    {
        var token = GithubConnectState.Create(42, "/projects/abc-123", installed, Now.AddMinutes(10), Secret);
        var verified = GithubConnectState.Validate(token, Now, Secret);

        Assert.NotNull(verified);
        Assert.Equal(42, verified!.OrgId);
        Assert.Equal("/projects/abc-123", verified.ReturnPath);
        Assert.Equal(installed, verified.Installed);
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        var token = GithubConnectState.Create(42, "/onboarding", false, Now.AddMinutes(-1), Secret);
        Assert.Null(GithubConnectState.Validate(token, Now, Secret));
    }

    [Fact]
    public void Rejects_a_tampered_token_or_wrong_secret()
    {
        var token = GithubConnectState.Create(42, "/onboarding", false, Now.AddMinutes(10), Secret);
        var parts = token.Split('.'); // org.expiry.encodedPath.installed.signature

        // Swap the org id but keep the original signature.
        Assert.Null(GithubConnectState.Validate($"99.{parts[1]}.{parts[2]}.{parts[3]}.{parts[4]}", Now, Secret));
        // Tamper the return path but keep the signature.
        Assert.Null(
            GithubConnectState.Validate($"{parts[0]}.{parts[1]}.{parts[2]}X.{parts[3]}.{parts[4]}", Now, Secret));
        // Flip the installed flag but keep the signature. Unsigned it would be a way to skip straight to
        // the pending-approval answer, which is only cosmetic, but a flag outside the signature is a
        // habit that stops being cosmetic the moment someone reads more into it.
        Assert.Null(
            GithubConnectState.Validate($"{parts[0]}.{parts[1]}.{parts[2]}.1.{parts[4]}", Now, Secret));
        // A different signing secret can't validate.
        Assert.Null(GithubConnectState.Validate(token, Now, "other-secret"));
        // Malformed inputs, including a token in the previous four-part shape.
        Assert.Null(GithubConnectState.Validate(null, Now, Secret));
        Assert.Null(GithubConnectState.Validate("not-a-token", Now, Secret));
        Assert.Null(
            GithubConnectState.Validate($"{parts[0]}.{parts[1]}.{parts[2]}.{parts[4]}", Now, Secret));
    }
}
