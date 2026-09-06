using System.Net.Http.Json;
using System.Text.Json;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Test helper: authenticate an <see cref="HttpClient"/> against the control-plane by signing up a
/// fresh user. The client keeps a cookie jar (WebApplicationFactory's default), so the resulting
/// session cookie carries to subsequent requests, and the caller is then authenticated. Returns the new
/// user's id + email.
///
/// <para>The user has NO org. Signup stopped creating one when ADR-0018 moved that into onboarding, so a
/// test that needs a tenant must POST /api/orgs as this client afterwards. Nothing here will fail if you
/// forget: the user simply belongs to nothing, which several endpoints report as a 404.</para>
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
