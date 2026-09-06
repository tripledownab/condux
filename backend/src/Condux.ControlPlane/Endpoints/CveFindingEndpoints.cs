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
        // org's GitHub installation token. Returns empty when GitHub is not connected, the App lacks the
        // Dependabot-alerts permission, or the repo has none.
        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/cve-findings",
                async Task<Results<Ok<IReadOnlyList<CveFindingWithExposure>>, NotFound>> (
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
                    var installs = await installations.GetByOrgAsync(project.OrgId, http.RequestAborted);
                    if (tokens is null || scanner is null || installs.Count == 0)
                    {
                        return TypedResults.Ok<IReadOnlyList<CveFindingWithExposure>>([]);
                    }

                    IReadOnlyList<CveFinding> findings;
                    try
                    {
                        var token = await tokens.GetAsync(installs[0].InstallationId, http.RequestAborted);
                        findings = await scanner.ScanAsync(token, repo.RepoFullName, http.RequestAborted);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Advisory feature: a GitHub hiccup or a missing permission yields no findings, not a 500.
                        logger.LogWarning(
                            ex, "cve-findings failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Ok<IReadOnlyList<CveFindingWithExposure>>([]);
                    }

                    return TypedResults.Ok(
                        await WithExposureAsync(projectId, findings, modules, logger, http.RequestAborted));
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "release-module exposure lookup failed project={ProjectId}", projectId);
        }

        return [.. findings.Select(
            finding => new CveFindingWithExposure(finding, ModuleExposures.For(finding, observed)))];
    }
}
