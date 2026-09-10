using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of org membership + RBAC + tenancy enforcement (#48): signup mints only the user
/// (ADR-0018) and the org is created afterwards, owner role included; non-members can't see or touch
/// another org's resources (404, existence hidden); role gates reads (member+) vs mutations (admin+);
/// and owner-only member management refuses to strand an org without an owner.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrgRbacTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<long> CreateOrgAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Signup_creates_no_org_and_creating_one_makes_you_its_owner()
    {
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);

        var orgs = await client.GetFromJsonAsync<JsonElement>("/api/orgs");
        Assert.Equal(0, orgs.GetArrayLength());

        var orgId = await CreateOrgAsync(client);
        orgs = await client.GetFromJsonAsync<JsonElement>("/api/orgs");
        Assert.Equal(1, orgs.GetArrayLength());
        Assert.Equal("owner", orgs[0].GetProperty("role").GetString());
        Assert.Equal(orgId, orgs[0].GetProperty("org").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Non_member_cannot_see_or_mutate_another_orgs_resources()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        var outsider = CreateClient();
        await ApiAuth.SignUpAsync(outsider);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/orgs/{orgId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/orgs/{orgId}/projects")).StatusCode);
        var create = await outsider.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "x", name = "X", platform = "go" });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Fact]
    public async Task Member_can_read_but_not_mutate_until_promoted_to_admin()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        var memberClient = CreateClient();
        var member = await ApiAuth.SignUpAsync(memberClient);

        // Seed the second user as a plain member (invites are #84; here we seed via the repo).
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(orgId, member.UserId, OrgRole.Member);

        // Member can read the org's projects...
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"/api/orgs/{orgId}/projects")).StatusCode);
        // ...but can't create one (needs admin+) → 403 (they know it exists, so not 404).
        var denied = await memberClient.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "p", name = "P", platform = "go" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Owner promotes them to admin (owner-only endpoint)...
        var promote = await owner.PatchAsJsonAsync($"/api/orgs/{orgId}/members/{member.UserId}",
            new { role = "admin" });
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);

        // ...now the mutation is allowed.
        var allowed = await memberClient.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "p", name = "P", platform = "go" });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Owner_sees_members_and_cannot_strand_the_org_without_an_owner()
    {
        var owner = CreateClient();
        var ownerUser = await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        var members = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/members");
        Assert.Equal(1, members.GetArrayLength());
        Assert.Equal("owner", members[0].GetProperty("role").GetString());

        // Can't remove the last owner, and can't demote them either.
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.DeleteAsync($"/api/orgs/{orgId}/members/{ownerUser.UserId}")).StatusCode);
        var demote = await owner.PatchAsJsonAsync($"/api/orgs/{orgId}/members/{ownerUser.UserId}",
            new { role = "member" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
    }
}
