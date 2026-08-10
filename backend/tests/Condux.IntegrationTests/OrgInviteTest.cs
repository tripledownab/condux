using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of org invites (#84): an admin+ invites an email; the invitee (logged in as that
/// email) redeems the token and joins with the invited role. Covers email-mismatch, invalid/revoked
/// tokens, and the no-privilege-escalation rule.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrgInviteTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<long> CreateOrgAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org", tier = 0 });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static string InviteEmail() => $"invitee-{Guid.NewGuid():N}@condux.test";

    [Fact]
    public async Task Invite_then_accept_joins_the_org_with_the_invited_role()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);
        var email = InviteEmail();

        // Owner invites the email as a member; the raw token comes back once, at creation.
        var inviteResp = await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email, role = "member" });
        Assert.Equal(HttpStatusCode.Created, inviteResp.StatusCode);
        var token = (await inviteResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        // It shows up as pending.
        var pending = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/invites");
        Assert.Equal(1, pending.GetArrayLength());

        // The invitee signs up as that email and accepts.
        var invitee = CreateClient();
        await ApiAuth.SignUpAsync(invitee, email);
        var accept = await invitee.PostAsJsonAsync("/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal("member", (await accept.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("role").GetString());

        // They're now a member of the invited org (plus their own personal org).
        var orgs = await invitee.GetFromJsonAsync<JsonElement>("/api/orgs");
        Assert.Contains(orgs.EnumerateArray(),
            o => o.GetProperty("org").GetProperty("id").GetInt64() == orgId);

        // The invite is consumed — accepting again fails, and it's no longer pending.
        var again = await invitee.PostAsJsonAsync("/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        var pendingAfter = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/invites");
        Assert.Equal(0, pendingAfter.GetArrayLength());
    }

    [Fact]
    public async Task Accepting_with_a_different_email_is_forbidden()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        var inviteResp = await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email = InviteEmail(), role = "member" });
        var token = (await inviteResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        // A different user (wrong email) can't redeem it.
        var stranger = CreateClient();
        await ApiAuth.SignUpAsync(stranger);
        var accept = await stranger.PostAsJsonAsync("/api/invites/accept", new { token });
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
    }

    [Fact]
    public async Task Revoked_and_unknown_tokens_are_rejected()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);
        var email = InviteEmail();

        var inviteResp = await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email, role = "member" });
        var created = await inviteResp.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("token").GetString();
        var inviteId = created.GetProperty("id").GetInt64();

        // Revoke it, then the invitee can't accept.
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/orgs/{orgId}/invites/{inviteId}")).StatusCode);

        var invitee = CreateClient();
        await ApiAuth.SignUpAsync(invitee, email);
        Assert.Equal(HttpStatusCode.NotFound,
            (await invitee.PostAsJsonAsync("/api/invites/accept", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await invitee.PostAsJsonAsync("/api/invites/accept", new { token = "nonsense" })).StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_invite_someone_as_owner()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        // Bring in an admin (invite + accept as member, then promote to admin).
        var adminEmail = InviteEmail();
        var invite = await (await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email = adminEmail, role = "member" })).Content.ReadFromJsonAsync<JsonElement>();
        var adminClient = CreateClient();
        var admin = await ApiAuth.SignUpAsync(adminClient, adminEmail);
        await adminClient.PostAsJsonAsync("/api/invites/accept",
            new { token = invite.GetProperty("token").GetString() });
        await owner.PatchAsJsonAsync($"/api/orgs/{orgId}/members/{admin.UserId}", new { role = "admin" });

        // The admin may invite a member, but not an owner (no privilege escalation).
        Assert.Equal(HttpStatusCode.Created, (await adminClient.PostAsJsonAsync(
            $"/api/orgs/{orgId}/invites", new { email = InviteEmail(), role = "member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.PostAsJsonAsync(
            $"/api/orgs/{orgId}/invites", new { email = InviteEmail(), role = "owner" })).StatusCode);
    }

    [Fact]
    public async Task Members_cannot_create_invites()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        var memberEmail = InviteEmail();
        var invite = await (await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email = memberEmail, role = "member" })).Content.ReadFromJsonAsync<JsonElement>();
        var memberClient = CreateClient();
        await ApiAuth.SignUpAsync(memberClient, memberEmail);
        await memberClient.PostAsJsonAsync("/api/invites/accept",
            new { token = invite.GetProperty("token").GetString() });

        // A plain member can't invite (needs admin+) → 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await memberClient.PostAsJsonAsync(
            $"/api/orgs/{orgId}/invites", new { email = InviteEmail(), role = "member" })).StatusCode);
    }
}
