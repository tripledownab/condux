using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.CveScanning;
using Condux.Core.SourceControl;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// A CVE finding together with what the runtime inventory says about it (ADR-0041). The finding comes
/// from a scanner and describes what a manifest declares; the exposure describes what was actually
/// seen running. They are kept apart rather than merged into one flat record because a scanner
/// produces one of them and never the other, and <see cref="CveFinding"/> travels on into the bump
/// path where exposure means nothing.
/// </summary>
public sealed record CveFindingWithExposure(CveFinding Finding, ModuleExposure Exposure);

/// <summary>
/// A repo's findings, and whether the scan stopped before the end. The list is not the total when
/// <see cref="Truncated"/> is set, and the dashboard says so rather than showing the count as complete.
/// <see cref="Unavailable"/> says why there are no findings at all when nothing could be read, which an
/// empty list alone would present as a repository with no vulnerabilities.
/// </summary>
public sealed record CveFindingsResponse(
    IReadOnlyList<CveFindingWithExposure> Findings, bool Truncated, CveScanUnavailable? Unavailable = null);

/// <summary>
/// The read side of the supply-chain surface (#117 slice 1): a linked repo's open CVE findings,
/// annotated with the runtime dependency inventory. Split from <c>RepoEndpoints</c> because CVE
/// scanning is its own domain, the same seam that already separates <see cref="CveFixEndpoints"/>.
/// Member+, and best-effort throughout: this surface is advisory, so a missing GitHub permission or
/// an unreachable store degrades what it can say rather than failing the page.
/// </summary>
internal static class CveFindingEndpoints
{
    public static void MapCveFindingEndpoints(this IEndpointRouteBuilder app)
    {
        // Open CVE findings for a linked repo (#117): the repo's open Dependabot alerts, fetched with the
        // org's GitHub installation token. Returns empty when the repo has none or the deployment has no
        // GitHub App, and empty with the reason when nothing could be read: the org has not connected
        // GitHub, alerts are switched off for the repository, the App lacks the Dependabot-alerts
        // permission, or any other failure. Those that attempted a read are also marked truncated, as they
        // were before the reason existed.
        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/cve-findings",
                async Task<Results<Ok<CveFindingsResponse>, NotFound>> (
                    long projectId, Guid repoId, HttpContext http, RepoLinkRepository repos,
                    ProjectRepository projects, GithubInstallationRepository installations,
                    ClickHouseReleaseModuleReader modules, ILoggerFactory loggerFactory) =>
                {
                    var logger = loggerFactory.CreateLogger(nameof(CveFindingEndpoints));
                    if (await repos.GetAsync(projectId, repoId, http.RequestAborted) is not { } repo
                        || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }

                    // Findings come from a scanner, never from one forge's API directly (ADR-0022), so the
                    // shape stays the same whichever produced them.
                    var tokens = http.RequestServices.GetService<ISourceHostTokens>();
                    var scanner = http.RequestServices.GetService<ICveScanner>();
                    // A deployment with no GitHub App has no scan to offer and nothing an org can do about
                    // it, so it names no reason; an org that has not connected the App can connect it.
                    if (tokens is null || scanner is null)
                    {
                        return TypedResults.Ok(new CveFindingsResponse([], Truncated: false));
                    }
                    var installs = await installations.GetByOrgAsync(project.OrgId, http.RequestAborted);
                    if (installs.Count == 0)
                    {
                        return TypedResults.Ok(new CveFindingsResponse(
                            [], Truncated: false, CveScanUnavailable.NotConnected));
                    }

                    CveScan scan;
                    try
                    {
                        var token = await tokens.GetAsync(installs[0].InstallationId, http.RequestAborted);
                        scan = await scanner.ScanAsync(token, repo.RepoFullName, http.RequestAborted);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
                    {
                        // Advisory feature: a GitHub hiccup or a missing permission yields no findings, not a 500.
                        // But not a complete answer either: the read stopped before it saw any alert, so the
                        // empty list is marked truncated and the dashboard says it could not read them all.
                        logger.LogWarning(
                            ex, "cve-findings failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Ok(new CveFindingsResponse(
                            [], Truncated: true,
                            ex is CveScanUnavailableException unavailable ? unavailable.Reason : CveScanUnavailable.Failed));
                    }

                    var findings = await WithExposureAsync(
                        projectId, scan.Findings, modules, logger, http.RequestAborted);
                    return TypedResults.Ok(new CveFindingsResponse(findings, scan.Truncated));
                })
            .WithName("cveFindings").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    /// <summary>
    /// Annotate each finding with what the inventory saw running (ADR-0041). One read covers every
    /// finding, so the annotation costs one query rather than one per advisory.
    ///
    /// <para>The inventory read has its own catch, deliberately outside the scan's. Folding it into
    /// that one would make an unreachable ClickHouse return <b>zero findings</b>, hiding live
    /// advisories because a secondary annotation failed. Losing the inventory instead leaves every
    /// finding reading Unknown, which is what Unknown means.</para>
    /// </summary>
    private static async Task<IReadOnlyList<CveFindingWithExposure>> WithExposureAsync(
        long projectId, IReadOnlyList<CveFinding> findings, ClickHouseReleaseModuleReader modules,
        ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<ObservedModule> observed = [];
        try
        {
            observed = await modules.ObservedAsync(
                // ClickHouse keys the inventory on the numeric project id as a string, the same key the
                // relay puts on the Kafka message and the consumer writes.
                projectId.ToString(CultureInfo.InvariantCulture),
                [.. findings.Select(finding => finding.Package)],
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex, "release-module exposure lookup failed project={ProjectId}", projectId);
        }

        return [.. findings.Select(
            finding => new CveFindingWithExposure(finding, ModuleExposures.For(finding, observed)))];
    }
}
