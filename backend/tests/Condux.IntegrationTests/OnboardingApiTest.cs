using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of onboarding completion (#132 follow-up): a fresh signup is not onboarded until it
/// POSTs /api/onboarding/complete, and accepting an invite marks the joiner onboarded automatically. The
/// dashboard gate reads this via <c>me.onboarded</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OnboardingApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private static async Task<bool> OnboardedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("onboarded").GetBoolean();

    [Fact]
    public async Task Fresh_signup_is_not_onboarded_until_complete_is_called()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);

        Assert.False(await OnboardedAsync(client));

        var complete = await client.PostAsync("/api/onboarding/complete", content: null);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        Assert.True(await OnboardedAsync(client));

        // Idempotent: completing again still succeeds and stays onboarded.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/onboarding/complete", content: null)).StatusCode);
        Assert.True(await OnboardedAsync(client));
    }

    [Fact]
    public async Task Accepting_an_invite_marks_the_invitee_onboarded()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgResp = await owner.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org", tier = 0 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var email = $"invitee-{Guid.NewGuid():N}@condux.test";
        var invite = await (await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email, role = "member" })).Content.ReadFromJsonAsync<JsonElement>();

        var invitee = CreateClient();
        await ApiAuth.SignUpAsync(invitee, email);
        Assert.False(await OnboardedAsync(invitee)); // a fresh account, not onboarded yet

        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsJsonAsync("/api/invites/accept",
            new { token = invite.GetProperty("token").GetString() })).StatusCode);

        // Joining an already-set-up org completes onboarding without the create-org/project flow.
        Assert.True(await OnboardedAsync(invitee));
    }
}
