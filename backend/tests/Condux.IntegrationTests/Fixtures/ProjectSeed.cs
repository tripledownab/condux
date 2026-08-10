using Condux.Storage.Postgres;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Seeds a real org + project directly via the repositories and returns the project's numeric id.
/// Tests that write issues need this so <c>issues.project_id</c> satisfies its FK to <c>projects(id)</c>
/// (#97). Requires the full migration set (projects table) to be applied first.
/// </summary>
public static class ProjectSeed
{
    public static async Task<long> CreateProjectAsync(string connectionString)
    {
        var org = await new OrgRepository(connectionString)
            .CreateAsync($"org-{Guid.NewGuid():N}", "Test Org", 0);
        var project = await new ProjectRepository(connectionString)
            .CreateAsync(org.Id, "Test Project", "other");
        return project.Id;
    }
}
