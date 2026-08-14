using Condux.Core.CveScanning;
using Condux.Core.SourceControl;

namespace Condux.Agent.Sandbox;

/// <summary>
/// Finds vulnerable dependencies by running osv-scanner over a repository's lockfiles in a container.
/// This is the forge-neutral path: it needs only the ability to read files, so it works the same on any
/// host we can read a repo from, where Dependabot works on exactly one.
///
/// The container reaches the network, because the scanner is useless without the advisory database it
/// queries. That is a deliberately different profile from a fix run: a pinned image, one fixed command,
/// lockfiles as its only input, and no credential inside it. The token stays here, on the host, and is
/// used only to fetch the files.
/// </summary>
public sealed class OsvCveScanner(
    ISourceHostClient repo, SandboxOptions options, string defaultBranch = "HEAD") : ICveScanner
{
    public string Name => OsvScanOutput.SourceName;

    public async Task<IReadOnlyList<CveFinding>> ScanAsync(
        string token, string repoFullName, CancellationToken cancellationToken = default)
    {
        var tree = await repo.ListTreeAsync(token, repoFullName, defaultBranch, cancellationToken);
        var lockfiles = Lockfiles.SelectFrom(tree);
        if (lockfiles.Count == 0)
        {
            // Nothing to scan is a clean result, not a failure: plenty of repositories have no lockfile.
            return [];
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in lockfiles)
        {
            if (await repo.GetFileAsync(token, repoFullName, path, defaultBranch, cancellationToken) is { } file)
            {
                files[path] = file.Content;
            }
        }

        if (files.Count == 0)
        {
            return [];
        }

        var scanning = options with { NetworkEnabled = true };
        using var docker = new DockerEngineClient(scanning);
        await using var workspace = await ContainerWorkspace.CreateAsync(
            docker, scanning, files, cancellationToken);

        // Scan the tree rather than each lockfile: one invocation, and the scanner already knows how to
        // recognize what it supports. Its exit code is non-zero when it *finds* something, so the report
        // is what matters here, not the status.
        // An absolute path, not a bare name: the scanner image ships its binary at the root and does not
        // put it on PATH, relying on an entrypoint we have to clear to keep the container alive.
        var result = await workspace.RunCommandAsync(
            ["/osv-scanner", "scan", "source", "--recursive", "--format=json", ContainerWorkspace.WorkDir],
            cancellationToken);

        return result.Output.Contains("\"results\"", StringComparison.Ordinal)
            ? OsvScanOutput.Parse(Report(result.Output))
            : throw new InvalidOperationException(
                $"osv-scanner produced no report (exit {result.ExitCode}): {Truncate(result.Output, 400)}");
    }

    /// <summary>
    /// The JSON report out of the command output. The scanner writes progress lines to stderr, which the
    /// workspace merges into the same stream, so the report starts at the first brace rather than the
    /// first byte.
    /// </summary>
    private static string Report(string output)
    {
        var start = output.IndexOf('{');
        return start <= 0 ? output : output[start..];
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
