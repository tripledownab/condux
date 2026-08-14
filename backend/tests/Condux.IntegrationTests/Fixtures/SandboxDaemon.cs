using Condux.Agent.Sandbox;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Where this machine's Docker daemon actually listens. Docker Desktop puts the socket under the user's
/// home rather than at the Linux default, so a test hardcoding one path passes on CI and fails on a
/// laptop. A deployment configures the endpoint explicitly; only tests have to go looking.
/// </summary>
public static class SandboxDaemon
{
    public static Uri Local()
    {
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 } configured)
        {
            return new Uri(configured);
        }

        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "run", "docker.sock");

        return new Uri(File.Exists(desktop) ? $"unix://{desktop}" : SandboxOptions.DefaultEndpoint);
    }
}
