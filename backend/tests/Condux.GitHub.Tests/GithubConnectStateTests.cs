using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

public class GithubConnectStateTests
{
    private const string Secret = "state-signing-secret";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void Round_trips_the_org_id_and_return_path_for_a_fresh_token()
    {
        var token = GithubConnectState.Create(42, "/projects/abc-123", Now.AddMinutes(10), Secret);
        var verified = GithubConnectState.Validate(token, Now, Secret);

        Assert.NotNull(verified);
        Assert.Equal(42, verified!.OrgId);
        Assert.Equal("/projects/abc-123", verified.ReturnPath);
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        var token = GithubConnectState.Create(42, "/onboarding", Now.AddMinutes(-1), Secret);
        Assert.Null(GithubConnectState.Validate(token, Now, Secret));
    }

    [Fact]
    public void Rejects_a_tampered_token_or_wrong_secret()
    {
        var token = GithubConnectState.Create(42, "/onboarding", Now.AddMinutes(10), Secret);
        var parts = token.Split('.'); // org.expiry.encodedPath.signature

        // Swap the org id but keep the original signature.
        Assert.Null(GithubConnectState.Validate($"99.{parts[1]}.{parts[2]}.{parts[3]}", Now, Secret));
        // Tamper the return path but keep the signature.
        Assert.Null(GithubConnectState.Validate($"{parts[0]}.{parts[1]}.{parts[2]}X.{parts[3]}", Now, Secret));
        // A different signing secret can't validate.
        Assert.Null(GithubConnectState.Validate(token, Now, "other-secret"));
        // Malformed inputs.
        Assert.Null(GithubConnectState.Validate(null, Now, Secret));
        Assert.Null(GithubConnectState.Validate("not-a-token", Now, Secret));
    }
}
