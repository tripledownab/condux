using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Full project CRUD (#98): get + rename + delete keyed by the project's public UUID (#125, the bigint id
/// never appears in a URL), tenancy-enforced. Delete cascades to the project's issues (the
/// <c>issues.project_id</c> FK, #97), and rename re-derives the slug uniquely within the org.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectCrudTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private static async Task<long> CreateOrgAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    // The numeric id (issue seeding / ingest identity) and the public UUID (the id used in every URL).
    private static async Task<(long Id, Guid PublicId)> CreateProjectAsync(HttpClient client, long orgId)
    {
        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        var project = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project");
        return (project.GetProperty("id").GetInt64(), project.GetProperty("publicId").GetGuid());
    }

    [Fact]
    public async Task Admin_can_rename_and_delete_a_project_and_delete_cascades_its_issues()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);
        var (projectId, publicId) = await CreateProjectAsync(owner, orgId);

        var rename = await owner.PatchAsJsonAsync($"/api/projects/{publicId}",
            new { name = "Renamed API", platform = "go" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = await rename.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed API", renamed.GetProperty("name").GetString());
        Assert.Equal("go", renamed.GetProperty("platform").GetString());
        Assert.Equal(publicId, renamed.GetProperty("publicId").GetGuid()); // public id is stable across a rename

        // Seed an issue so we can prove delete cascades it (issues.project_id FK, #97).
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-del", "Err", "run"), Level.Error, DateTimeOffset.UtcNow);
        Assert.Equal(1, await IssueCountAsync(projectId));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/projects/{publicId}")).StatusCode);
        Assert.Equal(0, await IssueCountAsync(projectId)); // cascade removed the issue
        // Deleting again is a 404 (already gone).
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/projects/{publicId}")).StatusCode);
    }

    [Fact]
    public async Task Get_by_public_id_returns_one_project_for_a_member_and_hides_it_from_non_members()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);
        var (projectId, publicId) = await CreateProjectAsync(owner, orgId);

        var ownerGet = await owner.GetAsync($"/api/projects/{publicId}");
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);
        var body = await ownerGet.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(projectId, body.GetProperty("id").GetInt64());
        Assert.Equal(publicId, body.GetProperty("publicId").GetGuid());
        Assert.Equal("Backend", body.GetProperty("name").GetString());

        // A plain member of the org can read it.
        var memberClient = CreateClient();
        var member = await ApiAuth.SignUpAsync(memberClient);
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(orgId, member.UserId, OrgRole.Member);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync($"/api/projects/{publicId}")).StatusCode);

        // A non-member gets 404 (existence hidden), and an unknown id is 404 too.
        var outsider = CreateClient();
        await ApiAuth.SignUpAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/projects/{publicId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/projects/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Member_cannot_mutate_and_non_member_gets_404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);
        var (_, publicId) = await CreateProjectAsync(owner, orgId);

        // A plain member: reads allowed, mutations 403 (they know it exists).
        var memberClient = CreateClient();
        var member = await ApiAuth.SignUpAsync(memberClient);
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(orgId, member.UserId, OrgRole.Member);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberClient.PatchAsJsonAsync($"/api/projects/{publicId}",
                new { name = "x", platform = "go" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberClient.DeleteAsync($"/api/projects/{publicId}")).StatusCode);

        // A non-member: 404 (existence hidden, not 403).
        var outsider = CreateClient();
        await ApiAuth.SignUpAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/api/projects/{publicId}")).StatusCode);
    }

    private async Task<int> IssueCountAsync(long projectId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM issues WHERE project_id = @p", conn);
        cmd.Parameters.AddWithValue("p", projectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
