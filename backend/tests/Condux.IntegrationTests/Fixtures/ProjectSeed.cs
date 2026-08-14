using Condux.Storage.Postgres;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Seeds a real org + project directly via the repositories and returns the project's numeric id.
/// Tests that write issues need this so <c>issues.project_id</c> satisfies its FK to <c>projects(id)</c>
/// (#97). Requires the full migration set (projects table) to be applied first.
/// </summary>
public static class ProjectSeed
{
    public static async Task<long> CreateProjectAsync(string connectionString) =>
        (await CreateOrgAndProjectAsync(connectionString)).ProjectId;

    /// <summary>
    /// Both ids, for tests that reach work through its org rather than its project — the runner lease
    /// claims per org, since a runner serves everything its org produces.
    /// </summary>
    public static async Task<(long OrgId, long ProjectId)> CreateOrgAndProjectAsync(string connectionString)
    {
        var org = await new OrgRepository(connectionString)
            .CreateAsync($"org-{Guid.NewGuid():N}", "Test Org", 0);
        var project = await new ProjectRepository(connectionString)
            .CreateAsync(org.Id, "Test Project", "other");
        return (org.Id, project.Id);
    }
}
