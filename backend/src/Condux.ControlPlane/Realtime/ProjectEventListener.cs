using Condux.Storage.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Condux.ControlPlane.Realtime;

/// <summary>Holds one dedicated Postgres <c>LISTEN</c> connection on the shared project-events channel
/// and fans each notification out to connected SSE clients via <see cref="ProjectEventHub"/> (ADR-0030).
/// Reconnects on any drop; a gap is harmless because the badge also polls as a safety net, so a missed
/// notify is caught late rather than lost. One idle connection per control-plane replica; every replica
/// listens, so Postgres delivers each NOTIFY to all of them.</summary>
public sealed class ProjectEventListener(
    string connectionString, ProjectEventHub hub, ILogger<ProjectEventListener> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(stoppingToken);
                conn.Notification += OnNotification;
                await using (var cmd = new NpgsqlCommand($"LISTEN {ProjectEventNotifier.Channel};", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }
                logger.LogInformation("listening for project events on {Channel}", ProjectEventNotifier.Channel);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "project-event LISTEN dropped; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        if (long.TryParse(e.Payload, out var projectId))
        {
            hub.Publish(projectId);
        }
    }
}
