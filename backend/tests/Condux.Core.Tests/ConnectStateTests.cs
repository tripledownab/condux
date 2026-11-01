using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class ConnectStateTests
{
    private const string Secret = "state-signing-secret";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Round_trips_the_org_id_return_path_and_installed_flag(bool installed)
    {
        var token = ConnectState.Create(ConnectState.GitHub, 42, "/projects/abc-123", installed, Now.AddMinutes(10), Secret);
        var verified = ConnectState.Validate(ConnectState.GitHub, token, Now, Secret);

        Assert.NotNull(verified);
        Assert.Equal(42, verified!.OrgId);
        Assert.Equal("/projects/abc-123", verified.ReturnPath);
        Assert.Equal(installed, verified.Installed);
    }

    // GitHub and Linear may sign under the same secret on a deployment that reuses one, so the purpose is
    // the only thing keeping a state from one flow out of the other's callback.
    [Fact]
    public void Rejects_a_token_minted_for_another_flow()
    {
        var token = ConnectState.Create(ConnectState.Linear, 42, "/projects/x", false, Now.AddMinutes(10), Secret);

        Assert.NotNull(ConnectState.Validate(ConnectState.Linear, token, Now, Secret));
        Assert.Null(ConnectState.Validate(ConnectState.GitHub, token, Now, Secret));
    }

    // A state minted by the build before the extraction must still verify during a deploy. The literal was
    // computed outside this code (HMAC-SHA256 over "connect:" plus the payload), so a change to the purpose
    // string, the payload layout or the encoding breaks it, where minting and validating with the same
    // constant would not.
    [Fact]
    public void Github_states_keep_the_wire_format_from_before_the_flow_was_shared()
    {
        const string Minted = "42.2000000000.L3Byb2plY3RzL3g.0.ea8aa17896a63caa678c65465ef583fdcb3456269d1e4c43e9b7047969d76832";
        var expiry = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

        Assert.Equal(Minted, ConnectState.Create(ConnectState.GitHub, 42, "/projects/x", false, expiry, Secret));
        Assert.Equal(42, ConnectState.Validate(ConnectState.GitHub, Minted, expiry.AddMinutes(-1), Secret)!.OrgId);
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        var token = ConnectState.Create(ConnectState.GitHub, 42, "/onboarding", false, Now.AddMinutes(-1), Secret);
        Assert.Null(ConnectState.Validate(ConnectState.GitHub, token, Now, Secret));
    }

    [Fact]
    public void Rejects_a_tampered_token_or_wrong_secret()
    {
        var token = ConnectState.Create(ConnectState.GitHub, 42, "/onboarding", false, Now.AddMinutes(10), Secret);
        var parts = token.Split('.'); // org.expiry.encodedPath.installed.signature

        // Swap the org id but keep the original signature.
        Assert.Null(ConnectState.Validate(ConnectState.GitHub, $"99.{parts[1]}.{parts[2]}.{parts[3]}.{parts[4]}", Now, Secret));
        // Tamper the return path but keep the signature.
        Assert.Null(
            ConnectState.Validate(ConnectState.GitHub, $"{parts[0]}.{parts[1]}.{parts[2]}X.{parts[3]}.{parts[4]}", Now, Secret));
        // Flip the installed flag but keep the signature. Unsigned it would be a way to skip straight to
        // the pending approval answer, which is only cosmetic, but a flag outside the signature is a
        // habit that stops being cosmetic the moment someone reads more into it.
        Assert.Null(
            ConnectState.Validate(ConnectState.GitHub, $"{parts[0]}.{parts[1]}.{parts[2]}.1.{parts[4]}", Now, Secret));
        // A different signing secret can't validate.
        Assert.Null(ConnectState.Validate(ConnectState.GitHub, token, Now, "other-secret"));
        // Malformed inputs, including a token in the previous four part shape.
        Assert.Null(ConnectState.Validate(ConnectState.GitHub, null, Now, Secret));
        Assert.Null(ConnectState.Validate(ConnectState.GitHub, "not-a-token", Now, Secret));
        Assert.Null(
            ConnectState.Validate(ConnectState.GitHub, $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[4]}", Now, Secret));
    }
}
