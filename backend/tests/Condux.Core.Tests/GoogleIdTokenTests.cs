using System.Text;
using System.Text.Json;
using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class GoogleIdTokenTests
{
    private const string Audience = "client-123.apps.googleusercontent.com";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    // A well-formed, unexpired token for our audience with a Google-verified email is accepted.
    [Fact]
    public void Validate_accepts_a_valid_token()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = "https://accounts.google.com",
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = true,
            email = "Ada@Example.com",
            sub = "google-sub-9",
        });

        var identity = GoogleIdToken.Validate(token, Audience, Now);

        Assert.NotNull(identity);
        Assert.Equal("google-sub-9", identity!.Subject);
        // The raw claim email is returned verbatim; the caller normalizes it.
        Assert.Equal("Ada@Example.com", identity.Email);
    }

    [Fact]
    public void Validate_accepts_email_verified_as_string()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = "accounts.google.com",
            exp = Now.ToUnixTimeSeconds() + 60,
            email_verified = "true",
            email = "ada@example.com",
            sub = "s1",
        });

        Assert.NotNull(GoogleIdToken.Validate(token, Audience, Now));
    }

    [Fact]
    public void Validate_rejects_a_token_for_another_audience()
    {
        var token = Token(new
        {
            aud = "someone-else.apps.googleusercontent.com",
            iss = "https://accounts.google.com",
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = true,
            email = "ada@example.com",
            sub = "s1",
        });

        Assert.Null(GoogleIdToken.Validate(token, Audience, Now));
    }

    [Fact]
    public void Validate_rejects_a_non_google_issuer()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = "https://evil.example.com",
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = true,
            email = "ada@example.com",
            sub = "s1",
        });

        Assert.Null(GoogleIdToken.Validate(token, Audience, Now));
    }

    [Fact]
    public void Validate_rejects_an_expired_token()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = "https://accounts.google.com",
            exp = Now.ToUnixTimeSeconds() - 3600,
            email_verified = true,
            email = "ada@example.com",
            sub = "s1",
        });

        Assert.Null(GoogleIdToken.Validate(token, Audience, Now));
    }

    [Fact]
    public void Validate_rejects_an_unverified_email()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = "https://accounts.google.com",
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = false,
            email = "ada@example.com",
            sub = "s1",
        });

        Assert.Null(GoogleIdToken.Validate(token, Audience, Now));
    }

    [Fact]
    public void Validate_rejects_a_token_missing_email_or_subject()
    {
        var noEmail = Token(new
        {
            aud = Audience,
            iss = "https://accounts.google.com",
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = true,
            sub = "s1",
        });
        Assert.Null(GoogleIdToken.Validate(noEmail, Audience, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!notbase64!!!.c")]
    public void Validate_rejects_malformed_tokens(string? token) =>
        Assert.Null(GoogleIdToken.Validate(token, Audience, Now));

    // Builds an unsigned JWT (header.payload.signature) — the validator reads the payload only, since the
    // token arrives over the direct TLS channel from Google's token endpoint (see GoogleIdToken remarks).
    private static string Token(object payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var body = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.signature-not-checked";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
