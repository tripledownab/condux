using Microsoft.Extensions.Configuration;

namespace Condux.Agent.Sandbox;

/// <summary>
/// Where the sandbox containers run and what they are allowed. The endpoint is configuration rather than
/// a hardcoded socket precisely so isolation is a deployment choice: point it at a local Docker socket for
/// convenience, or at a dedicated host to keep the container runtime on separate hardware from the rest of
/// the deployment. Nothing above this record knows which it is.
/// </summary>
public sealed record SandboxOptions(
    Uri Endpoint,
    string Image,
    long MemoryBytes,
    int PidsLimit,
    TimeSpan CommandTimeout)
{
    /// <summary>
    /// Whether the container may reach the network. Off for a fix run, which executes commands a model
    /// wrote against source code it read, and where reachable network is an exfiltration path. On only for
    /// a vulnerability scan, which is a different profile entirely: a pinned image running one fixed
    /// command over lockfiles, holding no credential, and useless without the advisory database it has to
    /// query. Keep these apart — collapsing them into one setting hands the fix sandbox a network it has
    /// no reason to have.
    /// </summary>
    public bool NetworkEnabled { get; init; }

    /// <summary>Docker's own default socket, used when only the image is configured.</summary>
    public const string DefaultEndpoint = "unix:///var/run/docker.sock";

    /// <summary>Whether the endpoint is a Unix socket rather than a TCP daemon.</summary>
    public bool IsUnixSocket => Endpoint.Scheme == "unix";

    /// <summary>The socket path, for a Unix endpoint.</summary>
    public string SocketPath => Endpoint.LocalPath;

    /// <summary>
    /// Read the sandbox configuration, or null when it is switched off. Off is the default: without an
    /// image there is nothing to run a command in, and the agent falls back to a workspace that reports it
    /// cannot execute. Partial configuration throws rather than silently degrading, matching how the
    /// GitHub App and object store are configured.
    /// </summary>
    public static SandboxOptions? FromEnv(IConfiguration configuration)
    {
        var image = configuration["CONDUX_SANDBOX_IMAGE"];
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var raw = configuration["CONDUX_SANDBOX_DOCKER_ENDPOINT"];
        var endpoint = string.IsNullOrWhiteSpace(raw) ? DefaultEndpoint : raw.Trim();

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("unix" or "tcp" or "http" or "https"))
        {
            throw new InvalidOperationException(
                $"CONDUX_SANDBOX_DOCKER_ENDPOINT must be a unix:// or tcp:// URI, got '{endpoint}'.");
        }

        return new SandboxOptions(
            uri,
            image.Trim(),
            MemoryBytes: Positive(configuration, "CONDUX_SANDBOX_MEMORY_MB", 2048) * 1024L * 1024L,
            PidsLimit: (int)Positive(configuration, "CONDUX_SANDBOX_PIDS", 512),
            CommandTimeout: TimeSpan.FromSeconds(Positive(configuration, "CONDUX_SANDBOX_TIMEOUT_SECONDS", 300)));
    }

    private static long Positive(IConfiguration configuration, string key, long fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return long.TryParse(raw, out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"{key} must be a positive integer, got '{raw}'.");
    }
}
