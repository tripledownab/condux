using System.Net.Http.Json;
using System.Text.Json;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Test helper: authenticate an <see cref="HttpClient"/> against the control-plane by signing up a
/// fresh user. The client keeps a cookie jar (WebApplicationFactory's default), so the resulting
/// session cookie carries to subsequent requests — the caller is then authenticated (and owner of a
/// freshly-minted personal org). Returns the new user's id + email.
/// </summary>
public static class ApiAuth
{
    public static Task<SignedUpUser> SignUpAsync(HttpClient client) =>
        SignUpAsync(client, $"u-{Guid.NewGuid():N}@condux.test");

    public static async Task<SignedUpUser> SignUpAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/signup",
            new { email, password = "test-password-123" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return new SignedUpUser(body.GetProperty("id").GetInt64(), email);
    }
}

public sealed record SignedUpUser(long UserId, string Email);
