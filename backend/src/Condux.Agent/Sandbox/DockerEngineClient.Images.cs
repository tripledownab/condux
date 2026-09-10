namespace Condux.Agent.Sandbox;

/// <summary>
/// The image half of the Docker client: making sure the daemon holds what a container is about to be
/// created from. Split out when the class passed the file-length limit, along the seam that was already
/// there, and it is the half with a policy rather than a call in it.
/// </summary>
public sealed partial class DockerEngineClient
{
    /// <summary>
    /// How many times a pull is attempted, and how long it waits between attempts.
    ///
    /// A pull is the only call here that leaves the machine, and the only one that is idempotent, so it
    /// is the only one retried. The observed failure is a registry timing out mid-handshake, which is
    /// transient by definition. Skipping the pull when the image is already present takes this off a
    /// developer's machine but does nothing for a CI runner, which holds no images and so pulls on every
    /// single run; the retry is what covers that.
    ///
    /// Retried in a loop here rather than through Microsoft.Extensions.Http.Resilience, which is what
    /// Condux.Storage uses for ClickHouse. That package attaches to an IHttpClientFactory registration
    /// and this client builds its own HttpClient, a blanket handler would retry container create and
    /// exec, which are not idempotent, and this project holds one package reference on purpose (see
    /// Condux.Agent.csproj). Internal so the test can assert against the same numbers the code uses.
    ///
    /// The delay is short because it is not what the waiting is made of: a handshake that times out has
    /// already spent 10 to 20 seconds before it throws.
    /// </summary>
    internal const int PullAttempts = 3;

    internal static readonly TimeSpan PullRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Make sure the image is on the daemon. Creating a container does not pull implicitly, so a host that
    /// has never run a sandbox fails its first run with a bare "No such image".
    ///
    /// Genuinely a no-op once present, which this comment claimed for a while before the code did it.
    /// /images/create reaches the registry even for an image already on disk, so every sandboxed run
    /// needed its registry to be reachable AND quick, and an integration test failed on a TLS handshake
    /// timeout against a tag that had been on the machine for months. An inspect is a local call.
    ///
    /// This is now the policy Docker itself applies when it creates a container: `docker create --pull`
    /// and `docker run --pull` both default to `missing`. So the consequence below is the one every
    /// Docker user already lives with, rather than one invented here.
    ///
    /// That consequence: an image already present is never refreshed, so a MOVING TAG STAYS at whatever
    /// the host first fetched. It is not hypothetical. The image is operator-supplied
    /// (CONDUX_SANDBOX_IMAGE) with nothing requiring a pinned tag, and the OSV scanner's own test names
    /// `ghcr.io/google/osv-scanner:latest`. A host that wants a moving tag followed has to pull it
    /// itself. Pin the tag when the version matters, which for a vulnerability scanner it does.
    /// </summary>
    public async Task EnsureImageAsync(string image, CancellationToken cancellationToken)
    {
        if (await HasImageAsync(image, cancellationToken))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await PullAsync(image, cancellationToken);
                return;
            }
            catch (Exception) when (attempt < PullAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PullRetryDelay, cancellationToken);
            }
        }
    }

    private async Task PullAsync(string image, CancellationToken cancellationToken)
    {
        // A tag is the last colon, unless that colon belongs to a registry port before the first slash.
        var colon = image.LastIndexOf(':');
        var tagged = colon > image.LastIndexOf('/');
        var name = tagged ? image[..colon] : image;
        var tag = tagged ? image[(colon + 1)..] : "latest";

        using var response = await http.PostAsync(
            $"/images/create?fromImage={Uri.EscapeDataString(name)}&tag={Uri.EscapeDataString(tag)}",
            content: null, cancellationToken);
        await ThrowOnFailureAsync(response, $"pull '{image}'", cancellationToken);

        // The pull streams progress and reports failure *inside* that stream while still answering 200, so
        // the body has to be read to the end and inspected rather than trusted. Reading it also waits for
        // the pull to finish, without which the very next create would race it.
        var progress = await response.Content.ReadAsStringAsync(cancellationToken);
        if (progress.Contains("\"error\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Docker could not pull '{image}': {progress}");
        }
    }

    /// <summary>
    /// Whether the daemon already holds this image. Inspect answers 200 or 404 and touches no registry.
    /// </summary>
    private async Task<bool> HasImageAsync(string image, CancellationToken cancellationToken)
    {
        // Escaped, because the name reaches a URL path and carries both a slash and a colon in the
        // general case. Measured against the daemon: it decodes %2F and %3A, so an escaped
        // `library/alpine:3.20` inspects the same image an unescaped one does.
        using var response = await http.GetAsync(
            $"/images/{Uri.EscapeDataString(image)}/json", cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
