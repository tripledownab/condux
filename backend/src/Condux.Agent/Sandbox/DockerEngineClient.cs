using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Condux.Core.FixEngine;

namespace Condux.Agent.Sandbox;

/// <summary>
/// The slice of the Docker Engine API a sandboxed run needs: create a container, put files in it, run a
/// command, take files out, destroy it. Thin and hand-rolled like the GitHub, Anthropic and Stripe clients,
/// because this is a small REST surface and an SDK would be a dependency for six calls.
///
/// Reaches the daemon over a Unix socket or TCP depending on configuration, which is what lets a
/// deployment put the container runtime on a different machine from the datastores.
///
/// Two pieces sit beside this file rather than in it, because together they took it past the length
/// limit: making sure an image is present, in DockerEngineClient.Images.cs, and decoding an exec's
/// output, in DockerExecStream.cs. The second is a separate type because it touches no daemon at all.
/// </summary>
public sealed partial class DockerEngineClient : IDisposable
{
    private readonly HttpClient http;

    public DockerEngineClient(SandboxOptions options)
    {
        http = options.IsUnixSocket ? OverUnixSocket(options.SocketPath) : OverTcp(options.Endpoint);
    }

    private static HttpClient OverUnixSocket(string path)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };

        // The host is ignored for a Unix socket but HttpClient still requires an absolute URI to build one.
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    private static HttpClient OverTcp(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "tcp" ? "http" : endpoint.Scheme };
        return new HttpClient { BaseAddress = builder.Uri };
    }

    /// <summary>
    /// Create a container that idles until commands are sent to it. Networking is off and memory, processes
    /// and writable layer size are capped: a fix run needs to compile and test, not to reach the internet,
    /// and an agent that spawns processes in a loop should hit a limit rather than the host's.
    /// </summary>
    public async Task<string> CreateAsync(SandboxOptions options, CancellationToken cancellationToken)
    {
        var request = new
        {
            Image = options.Image,
            // Clear the image's entrypoint. A tool image usually sets one (the osv-scanner image runs
            // /osv-scanner), and leaving it in place turns the idle command below into arguments to that
            // tool, so the container exits immediately and every exec afterwards fails on a dead container.
            Entrypoint = Array.Empty<string>(),
            // Idle in the foreground so the container stays up for exec without running anything itself.
            Cmd = new[] { "sleep", "infinity" },
            WorkingDir = ContainerWorkspace.WorkDir,
            // Off unless the workload genuinely needs it, which today means only a vulnerability scan
            // querying the advisory database. Everything else runs with no route out.
            NetworkDisabled = !options.NetworkEnabled,
            HostConfig = new
            {
                NetworkMode = options.NetworkEnabled ? "bridge" : "none",
                Memory = options.MemoryBytes,
                PidsLimit = options.PidsLimit,
                AutoRemove = false,
                CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges" },
                // Not bounded: how much a command may write to disk. Docker refuses --storage-opt size on
                // every storage driver except overlay over xfs with pquota, and mounting the workspace as
                // tmpfs bounds it at the cost of noexec, which stops a checkout running its own tooling.
                // What limits the exposure instead is that the container is one per run and destroyed
                // after it, so a runaway write is reclaimed rather than permanent. An operator who wants a
                // hard ceiling gets one by running this daemon on xfs with pquota, or on its own host.
            },
        };

        using var response = await http.PostAsJsonAsync("/containers/create", request, cancellationToken);
        await ThrowOnFailureAsync(response, "create container", cancellationToken);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return created.GetProperty("Id").GetString()
            ?? throw new InvalidOperationException("Docker returned a container with no id.");
    }


    public async Task StartAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            $"/containers/{containerId}/start", content: null, cancellationToken);
        await ThrowOnFailureAsync(response, "start container", cancellationToken);
    }

    /// <summary>
    /// Remove the container and its writable layer. Best effort by design: a run that already failed should
    /// not fail again on cleanup, and an orphaned container is a visible operational problem rather than a
    /// correctness one.
    /// </summary>
    public async Task RemoveAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.DeleteAsync(
                $"/containers/{containerId}?force=true&v=true", cancellationToken);
        }
        catch (Exception)
        {
            // Swallowed deliberately; the caller is disposing.
        }
    }

    /// <summary>Run an argument vector in the container and capture what it printed.</summary>
    public async Task<CommandResult> ExecAsync(
        string containerId, IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        var create = new
        {
            Cmd = argv,
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            WorkingDir = ContainerWorkspace.WorkDir,
        };

        using var createResponse = await http.PostAsJsonAsync(
            $"/containers/{containerId}/exec", create, cancellationToken);
        await ThrowOnFailureAsync(createResponse, "create exec", cancellationToken);

        var execId = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .GetProperty("Id").GetString()
            ?? throw new InvalidOperationException("Docker returned an exec with no id.");

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"/exec/{execId}/start")
        {
            Content = JsonContent.Create(new { Detach = false, Tty = false }),
        };
        using var startResponse = await http.SendAsync(
            startRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowOnFailureAsync(startResponse, "start exec", cancellationToken);

        var output = await DockerExecStream.ReadMultiplexedAsync(
            await startResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);

        using var inspect = await http.GetAsync($"/exec/{execId}/json", cancellationToken);
        await ThrowOnFailureAsync(inspect, "inspect exec", cancellationToken);
        var exitCode = (await inspect.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .GetProperty("ExitCode").GetInt32();

        return new CommandResult(exitCode, output);
    }

    /// <summary>Upload a tar archive, expanded at the given absolute path inside the container.</summary>
    public async Task PutArchiveAsync(
        string containerId, string path, byte[] tar, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(tar);
        using var response = await http.PutAsync(
            $"/containers/{containerId}/archive?path={Uri.EscapeDataString(path)}", content, cancellationToken);
        await ThrowOnFailureAsync(response, "upload archive", cancellationToken);
    }

    /// <summary>Download one path from the container as a tar archive, or null when it does not exist.</summary>
    public async Task<byte[]?> GetArchiveAsync(
        string containerId, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            $"/containers/{containerId}/archive?path={Uri.EscapeDataString(path)}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task ThrowOnFailureAsync(
        HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Docker could not {what}: {(int)response.StatusCode} {body}");
    }

    public void Dispose() => http.Dispose();
}
