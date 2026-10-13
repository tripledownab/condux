using System.Net;
using System.Net.Http.Json;
using Condux.Core.FixEngine;

namespace Condux.Runner;

/// <summary>
/// One job as the control plane hands it over. Shaped after the lease response rather than sharing
/// a type with
/// it: a runner is deployed by the customer and lags our deploys, so the two shapes have to be free to
/// differ. Anything added on the server must stay optional here, and a field this runner does not know
/// about is ignored rather than fatal.
/// </summary>
public sealed record RunnerJob(
    Guid FixId, long IssueId, string RepoFullName, string BaseBranch, string Prompt,
    IReadOnlyList<string> ScopedPaths, DateTimeOffset LeaseExpiresAt, string LeaseId,
    string? Ref = null)
{
    /// <summary>What names the work in branch names and pull-request copy: the server's display ref (a
    /// GHSA id for a CVE bump), else the issue id from a server that predates the field.</summary>
    public string WorkRef => string.IsNullOrEmpty(Ref) ? IssueId.ToString() : Ref;
}

/// <summary>
/// The runner's side of the lease protocol: take a job, prove it is still ours, report what happened.
/// Every call is outbound over HTTPS and authenticated by the runner token, so a customer needs no
/// inbound firewall rule and we never reach into their network.
/// </summary>
public sealed class LeaseClient(HttpClient http)
{
    /// <summary>Take the next job, or null when there is none. Null is the ordinary answer and must not
    /// read as an error: most polls of a healthy queue find nothing.</summary>
    public async Task<RunnerJob?> TryLeaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/runner/lease", content: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RunnerJob>(cancellationToken);
    }

    /// <summary>Extend the lease. False means it is gone and the work belongs to someone else now.</summary>
    public async Task<bool> HeartbeatAsync(RunnerJob job, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/runner/jobs/{job.FixId}/heartbeat", new { leaseId = job.LeaseId }, cancellationToken);
        return ReadLeaseAnswer(response);
    }

    /// <summary>
    /// Report the outcome. Refused (false) when the lease has been lost, which is not an error to retry:
    /// the runner that holds the job now will report it, and writing over that would replace a real
    /// outcome with a stale one.
    /// </summary>
    public async Task<bool> ReportAsync(
        RunnerJob job, FixStatus status, FixResult? result, string model, string summary,
        FixFailureReason? failureReason = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/runner/jobs/{job.FixId}/result",
            new
            {
                leaseId = job.LeaseId,
                status = (int)status,
                branch = result?.Branch ?? "",
                prUrl = result?.PrUrl ?? "",
                summary = result?.Summary ?? summary,
                model,
                inputTokens = result?.InputTokens ?? 0,
                outputTokens = result?.OutputTokens ?? 0,
                failureReason = (int?)failureReason,
            },
            cancellationToken);
        return ReadLeaseAnswer(response);
    }

    // 409 is the control plane saying the lease is gone, and 401 that the runner's token was revoked.
    // Anything else, such as a 502 from the edge while the control plane redeploys, throws, so the caller
    // retries it rather than abandoning a run, or a report, that the lease still covers.
    private static bool ReadLeaseAnswer(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }
}
