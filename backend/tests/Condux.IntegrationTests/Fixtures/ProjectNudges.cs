using System.Globalization;
using Condux.Storage.Postgres;
using Npgsql;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Catches the live-stream pings (ADR-0030) an action sends: listens on the project-events channel, runs
/// the action, and reports which project ids were pinged within a few seconds of it.
/// </summary>
public static class ProjectNudges
{
    /// <summary>Whether <paramref name="action"/> pinged <paramref name="projectId"/>.</summary>
    public static async Task<bool> IsSentAsync(string connectionString, long projectId, Func<Task> action)
    {
        await using var listener = new NpgsqlConnection(connectionString);
        await listener.OpenAsync();
        var payloads = new List<string>();
        listener.Notification += (_, e) => payloads.Add(e.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {ProjectEventNotifier.Channel}", listener))
        {
            await listen.ExecuteNonQueryAsync();
        }

        await action();

        // Reads what has arrived, waiting up to a second at a time and five in all.
        var expected = projectId.ToString(CultureInfo.InvariantCulture);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!payloads.Contains(expected) && DateTime.UtcNow < deadline)
        {
            await listener.WaitAsync(1000);
        }
        return payloads.Contains(expected);
    }
}
