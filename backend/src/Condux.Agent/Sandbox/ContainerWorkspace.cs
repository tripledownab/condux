using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent.Sandbox;

/// <summary>
/// A checkout inside an ephemeral container: one per run, network disabled, resource capped, destroyed on
/// disposal. This is the workspace that can execute, so it is what turns the agentic loop from a model
/// that proposes an edit into one that can check its own work.
///
/// The container never holds a source-host token. Files are placed into it by the host, which is the only
/// side that holds the credential, and every branch, commit and pull request still happens host side
/// through the source-host client. Nothing inside the sandbox can push, whatever it runs.
/// </summary>
public sealed class ContainerWorkspace : IAgentWorkspace, IAsyncDisposable
{
    /// <summary>Where the checkout lives inside the container, and the working directory for commands.</summary>
    public const string WorkDir = "/workspace";

    private readonly DockerEngineClient docker;
    private readonly SandboxOptions options;
    private readonly string containerId;
    private readonly Dictionary<string, string> changed = [];
    private readonly HashSet<string> known;

    private ContainerWorkspace(
        DockerEngineClient docker, SandboxOptions options, string containerId, IEnumerable<string> seeded)
    {
        this.docker = docker;
        this.options = options;
        this.containerId = containerId;
        known = [.. seeded];
    }

    public bool CanRunCommands => true;

    public IReadOnlyDictionary<string, string> ChangedFiles => changed;

    /// <summary>
    /// Bring up a container and place the checkout in it. The caller disposes the workspace, which destroys
    /// the container: a run that throws must not leave one behind, so construction and teardown are paired
    /// rather than left to the orchestrator to remember.
    /// </summary>
    public static async Task<ContainerWorkspace> CreateAsync(
        DockerEngineClient docker,
        SandboxOptions options,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken = default)
    {
        await docker.EnsureImageAsync(options.Image, cancellationToken);
        var containerId = await docker.CreateAsync(options, cancellationToken);

        try
        {
            await docker.StartAsync(containerId, cancellationToken);
            await docker.PutArchiveAsync(
                containerId, WorkDir, SandboxTar.FromFiles(files), cancellationToken);
        }
        catch
        {
            // Started but unusable is still a container to clean up.
            await docker.RemoveAsync(containerId, CancellationToken.None);
            throw;
        }

        return new ContainerWorkspace(docker, options, containerId, files.Keys);
    }

    /// <summary>The paths placed in the workspace, plus anything the agent has since created.</summary>
    public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. known.Order()]);

    /// <summary>
    /// Read from the container rather than from a staged copy, so the agent sees the file as it actually is
    /// after anything a command did to it.
    /// </summary>
    public async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var archive = await docker.GetArchiveAsync(
            containerId, Absolute(RepoPaths.Normalize(path)), cancellationToken);
        return archive is null ? null : SandboxTar.FirstFileContents(archive);
    }

    public async Task WriteFileAsync(
        string path, string contents, CancellationToken cancellationToken = default)
    {
        // The model chooses this path, so it is checked here exactly as the in-memory workspace checks it.
        // This was the only workspace that staged a path unvalidated, and the staged key is what the host
        // later commits, so an unchecked one would have travelled all the way to the git tail.
        var normalized = RepoPaths.Normalize(path);
        await docker.PutArchiveAsync(
            containerId, WorkDir,
            SandboxTar.FromFiles(new Dictionary<string, string> { [normalized] = contents }),
            cancellationToken);

        changed[normalized] = contents;
        known.Add(normalized);
    }

    /// <summary>
    /// Run an authorized command, bounded by its own timeout. The timeout is the workspace's job because
    /// the workspace owns the process: a test suite that hangs would otherwise stall the run until the
    /// whole job was cancelled, and the model would learn nothing from it.
    /// </summary>
    public async Task<CommandResult> RunCommandAsync(
        IReadOnlyList<string> argv, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.CommandTimeout);

        try
        {
            return await docker.ExecAsync(containerId, argv, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The command outran its budget, not the run. Reported like any other failing command so the
            // agent can try something cheaper instead of the run dying here.
            return new CommandResult(
                124, $"Command exceeded the {options.CommandTimeout.TotalSeconds:0} second limit.");
        }
    }

    private static string Absolute(string path) => $"{WorkDir}/{path.TrimStart('/')}";

    public async ValueTask DisposeAsync() =>
        await docker.RemoveAsync(containerId, CancellationToken.None);
}
