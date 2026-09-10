using Xunit;

namespace Condux.GitHub.Tests;

public class GithubSelectionTokenTests
{
    private const string Secret = "state-signing-secret";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static readonly GithubSelectionCandidate[] Candidates =
    [
        new(7, "acme"),
        new(9, "acme-labs"),
    ];

    [Fact]
    public void Round_trips_the_org_and_every_candidate_for_a_fresh_token()
    {
        var token = GithubSelectionToken.Create(42, Candidates, Now.AddMinutes(10), Secret);
        var selection = GithubSelectionToken.Validate(token, Now, Secret);

        Assert.NotNull(selection);
        Assert.Equal(42, selection!.OrgId);
        Assert.Equal([7, 9], selection.Candidates.Select(c => c.InstallationId));
        Assert.Equal(["acme", "acme-labs"], selection.Candidates.Select(c => c.AccountLogin));
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        var token = GithubSelectionToken.Create(42, Candidates, Now.AddMinutes(-1), Secret);
        Assert.Null(GithubSelectionToken.Validate(token, Now, Secret));
    }

    // The whole point of signing is that the browser holds this token between two requests: an unsigned
    // list would let anyone name an installation id and have us link it to their org.
    [Fact]
    public void Rejects_a_payload_edited_to_add_an_installation()
    {
        var token = GithubSelectionToken.Create(42, Candidates, Now.AddMinutes(10), Secret);
        var forged = GithubSelectionToken.Create(
            42, [new GithubSelectionCandidate(999, "someone-else")], Now.AddMinutes(10), "other-secret");

        // The forged payload with the real token's signature, and vice versa.
        Assert.Null(GithubSelectionToken.Validate(
            $"{forged.Split('.')[0]}.{token.Split('.')[1]}", Now, Secret));
        Assert.Null(GithubSelectionToken.Validate(forged, Now, Secret));
    }

    [Fact]
    public void Rejects_a_wrong_secret_or_a_malformed_token()
    {
        var token = GithubSelectionToken.Create(42, Candidates, Now.AddMinutes(10), Secret);

        Assert.Null(GithubSelectionToken.Validate(token, Now, "other-secret"));
        Assert.Null(GithubSelectionToken.Validate(null, Now, Secret));
        Assert.Null(GithubSelectionToken.Validate("not-a-token", Now, Secret));
        Assert.Null(GithubSelectionToken.Validate("too.many.parts", Now, Secret));
    }

    // Both tokens ride the same connect flow and are signed with the same secret, so one must never be
    // accepted where the other is expected.
    [Fact]
    public void Does_not_validate_a_connect_state_token()
    {
        var connect = GithubConnectState.Create(42, "/projects/x", false, Now.AddMinutes(10), Secret);
        Assert.Null(GithubSelectionToken.Validate(connect, Now, Secret));

        var selection = GithubSelectionToken.Create(42, Candidates, Now.AddMinutes(10), Secret);
        Assert.Null(GithubConnectState.Validate(selection, Now, Secret));
    }
}
