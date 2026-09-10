using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Per-issue notes over the control-plane: add/list/delete, empty-body rejection, org tenancy (a
/// non-member cannot see them), and the author-or-admin delete guard (a plain member cannot delete
/// someone else's note). Issues come from ingest, so they are seeded directly via the repository.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueNotesApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<(HttpClient Client, long OrgId, long ProjectId, Guid IssueId)> ProvisionWithIssueAsync()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        var upsert = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-note", "TypeError: boom", "app.py:1"), Level.Error, DateTimeOffset.UtcNow);
        return (client, orgId, projectId, upsert.PublicId);
    }

    [Fact]
    public async Task Adds_lists_and_deletes_a_note()
    {
        var (client, _, projectId, issueId) = await ProvisionWithIssueAsync();
        var basePath = $"/api/projects/{projectId}/issues/{issueId}/notes";

        var add = await client.PostAsJsonAsync(basePath, new { body = "  looks like a null deref  " });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var created = await add.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("looks like a null deref", created.GetProperty("body").GetString()); // trimmed server-side
        Assert.EndsWith("@condux.test", created.GetProperty("authorEmail").GetString());
        var noteId = created.GetProperty("id").GetString();

        var list = await client.GetFromJsonAsync<JsonElement>(basePath);
        Assert.Equal(1, list.GetArrayLength());

        var del = await client.DeleteAsync($"{basePath}/{noteId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>(basePath);
        Assert.Equal(0, after.GetArrayLength());
    }

    [Fact]
    public async Task Rejects_an_empty_note()
    {
        var (client, _, projectId, issueId) = await ProvisionWithIssueAsync();
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueId}/notes", new { body = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("note_body_required", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Hides_notes_from_non_members()
    {
        var (_, _, projectId, issueId) = await ProvisionWithIssueAsync();

        var outsider = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(outsider); // a different user, their own org — not a member here
        var resp = await outsider.GetAsync($"/api/projects/{projectId}/issues/{issueId}/notes");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_delete_another_users_note()
    {
        var (owner, orgId, projectId, issueId) = await ProvisionWithIssueAsync();
        var basePath = $"/api/projects/{projectId}/issues/{issueId}/notes";

        var add = await owner.PostAsJsonAsync(basePath, new { body = "owner note" });
        var noteId = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // A second user joins the org as a plain member via the invite flow.
        var member = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        var memberUser = await ApiAuth.SignUpAsync(member, $"member-{Guid.NewGuid():N}@condux.test");
        var invite = await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email = memberUser.Email, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        (await member.PostAsJsonAsync("/api/invites/accept", new { token })).EnsureSuccessStatusCode();

        // The member is neither the author nor an admin → forbidden, and the note survives.
        var del = await member.DeleteAsync($"{basePath}/{noteId}");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        var list = await owner.GetFromJsonAsync<JsonElement>(basePath);
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public async Task An_MCP_note_has_no_user_author_so_only_an_admin_can_delete_it()
    {
        var (owner, orgId, projectId, issueId) = await ProvisionWithIssueAsync();
        var basePath = $"/api/projects/{projectId}/issues/{issueId}/notes";

        // An agent leaves the note over MCP (ADR-0046), so its author is a token, not a person.
        var mint = await owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/mcp-tokens", new { name = "Claude", capability = "triage" });
        var raw = (await mint.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var agent = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        agent.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", raw);
        (await agent.PostAsJsonAsync("/api/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "add_issue_note",
                arguments = new { issueId = issueId.ToString(), body = "agent note" },
            },
        })).EnsureSuccessStatusCode();

        var created = (await owner.GetFromJsonAsync<JsonElement>(basePath))[0];
        var noteId = created.GetProperty("id").GetString();
        Assert.Equal("Claude", created.GetProperty("authorTokenName").GetString());

        // Nobody is the author, so the author branch of the delete guard matches no one and a plain
        // member is refused. This is the pre-existing rule meeting a null author, not a new rule.
        var member = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        var memberUser = await ApiAuth.SignUpAsync(member, $"member-{Guid.NewGuid():N}@condux.test");
        var invite = await owner.PostAsJsonAsync($"/api/orgs/{orgId}/invites",
            new { email = memberUser.Email, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        (await member.PostAsJsonAsync("/api/invites/accept", new { token })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"{basePath}/{noteId}")).StatusCode);

        // An admin can moderate it away, so an agent's note is never unremovable.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{basePath}/{noteId}")).StatusCode);
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonElement>(basePath)).GetArrayLength());
    }
}
