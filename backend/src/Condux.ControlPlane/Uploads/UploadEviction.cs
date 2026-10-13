using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Uploads;

/// <summary>
/// The step both upload endpoints take after an artifact is stored: evict the org's oldest artifacts,
/// from any of its projects, past its tier's quota. Best effort, because the upload has already
/// succeeded and CI must not be told otherwise; a failed eviction is caught up by the next upload. No
/// token: a client that hangs up as the upload finishes must not leave the org past its quota.
/// </summary>
public static class UploadEviction
{
    public static async Task TryEvictAsync(HttpContext http, long projectId)
    {
        try
        {
            await http.RequestServices.GetRequiredService<UploadStorage>()
                .EvictBeyondQuotaAsync(projectId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(UploadEviction))
                .LogWarning(ex, "upload eviction failed project={ProjectId}", projectId);
        }
    }
}
