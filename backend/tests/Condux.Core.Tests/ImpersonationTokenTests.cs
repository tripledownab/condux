using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

// The signed impersonation token (ADR-0027): it must round-trip the target org, but only for the exact
// admin it was minted for, and reject anything tampered, expired, or wrong-keyed. Binding to the admin id
// is what makes a leaked cookie useless on its own.
public class ImpersonationTokenTests
{
    private const string Secret = "impersonation-signing-secret";
    private const long Admin = 7;
    private const long Org = 42;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void Round_trips_the_org_id_for_the_minting_admin()
    {
        var token = ImpersonationToken.Create(Admin, Org, Now.AddMinutes(30), Secret);
        Assert.Equal(Org, ImpersonationToken.Validate(token, Admin, Now, Secret));
    }

    [Fact]
    public void Rejects_a_different_admin_than_it_was_minted_for()
    {
        var token = ImpersonationToken.Create(Admin, Org, Now.AddMinutes(30), Secret);
        // A leaked cookie presented under a different admin's session grants nothing.
        Assert.Null(ImpersonationToken.Validate(token, Admin + 1, Now, Secret));
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        var token = ImpersonationToken.Create(Admin, Org, Now.AddMinutes(-1), Secret);
        Assert.Null(ImpersonationToken.Validate(token, Admin, Now, Secret));
    }

    [Fact]
    public void Rejects_a_tampered_payload_or_wrong_secret_or_malformed_input()
    {
        var token = ImpersonationToken.Create(Admin, Org, Now.AddMinutes(30), Secret);
        var parts = token.Split('.');

        // Swap the target org but keep the original signature.
        Assert.Null(ImpersonationToken.Validate($"{parts[0]}.99.{parts[2]}.{parts[3]}", Admin, Now, Secret));
        // A different signing secret can't validate.
        Assert.Null(ImpersonationToken.Validate(token, Admin, Now, "other-secret"));
        // Malformed inputs.
        Assert.Null(ImpersonationToken.Validate(null, Admin, Now, Secret));
        Assert.Null(ImpersonationToken.Validate("not-a-token", Admin, Now, Secret));
    }

    [Fact]
    public void Rejects_a_github_connect_style_token_domain_separation()
    {
        // A 3-part connect-state-shaped token ("{org}.{expiry}.{sig}") must not validate as impersonation
        // (different arity + domain-separated HMAC prefix), so the two token kinds can never be confused.
        var connectShaped = $"{Org}.{Now.AddMinutes(30).ToUnixTimeSeconds()}.deadbeef";
        Assert.Null(ImpersonationToken.Validate(connectShaped, Admin, Now, Secret));
    }
}
