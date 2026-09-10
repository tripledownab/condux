using System.Diagnostics;
using Condux.Agent.Sandbox;
using Condux.Core.FixEngine;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The sandboxed workspace against a real Docker daemon. Everything else about the agentic loop tests with
/// no container, but the isolation properties cannot be asserted against a fake: whether the network is
/// actually off and whether the container is actually destroyed are facts about the daemon, not about our
/// code's intentions. Uses a stock alpine image so the test pulls a few megabytes rather than a toolchain.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContainerWorkspaceTest
{
    private static readonly SandboxOptions Options = new(
        SandboxDaemon.Local(),
        Image: "alpine:3.20",
        MemoryBytes: 512L * 1024 * 1024,
        PidsLimit: 128,
        CommandTimeout: TimeSpan.FromSeconds(60));

    private static readonly Dictionary<string, string> Repo = new()
    {
        ["src/cart.ts"] = "export const items = [];",
        ["package.json"] = "{ \"name\": \"checkout\" }",
    };

    /// <summary>
    /// The allow list exists for model-authored commands; these are ours, so they go straight to the
    /// daemon. Keeping that distinction explicit stops the test from quietly proving the wrong thing.
    /// </summary>
    private static async Task<CommandResult> RunAsync(ContainerWorkspace workspace, params string[] argv) =>
        await workspace.RunCommandAsync(argv);

    [Fact]
    public async Task Seeded_files_are_readable_inside_the_container()
    {
        using var docker = new DockerEngineClient(Options);
        await using var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);

        Assert.Equal("export const items = [];", await workspace.ReadFileAsync("src/cart.ts"));
        Assert.Null(await workspace.ReadFileAsync("src/missing.ts"));
    }

    [Fact]
    public async Task A_command_runs_against_the_seeded_checkout()
    {
        using var docker = new DockerEngineClient(Options);
        await using var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);

        var result = await RunAsync(workspace, "cat", "src/cart.ts");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("export const items", result.Output);
    }

    [Fact]
    public async Task A_failing_command_reports_its_exit_code_and_its_stderr()
    {
        using var docker = new DockerEngineClient(Options);
        await using var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);

        var result = await RunAsync(workspace, "cat", "src/missing.ts");

        Assert.NotEqual(0, result.ExitCode);
        // stderr is merged into the output, so the model sees why it failed rather than an empty string.
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task A_write_is_visible_to_a_later_command_and_staged_for_the_pull_request()
    {
        using var docker = new DockerEngineClient(Options);
        await using var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);

        await workspace.WriteFileAsync("src/cart.ts", "export const items = [1];");
        var result = await RunAsync(workspace, "cat", "src/cart.ts");

        // Both halves matter: the container must see the edit so the agent can test it, and the host must
        // have it staged, because the host is what opens the pull request.
        Assert.Contains("[1]", result.Output);
        Assert.Equal("export const items = [1];", workspace.ChangedFiles["src/cart.ts"]);
    }

    [Fact]
    public async Task The_sandbox_has_no_network()
    {
        using var docker = new DockerEngineClient(Options);
        await using var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);

        // Resolving a name needs DNS, which needs a network. Asserted against the daemon because a config
        // flag we set is not evidence the daemon honoured it.
        var result = await RunAsync(workspace, "ping", "-c", "1", "-W", "2", "1.1.1.1");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task A_command_that_outruns_its_budget_is_reported_rather_than_hanging_the_run()
    {
        var impatient = Options with { CommandTimeout = TimeSpan.FromSeconds(2) };
        using var docker = new DockerEngineClient(impatient);
        await using var workspace = await ContainerWorkspace.CreateAsync(docker, impatient, Repo);

        var result = await RunAsync(workspace, "sleep", "30");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("limit", result.Output);
    }

    [Fact]
    public async Task Disposing_the_workspace_destroys_the_container()
    {
        using var docker = new DockerEngineClient(Options);
        var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);

        await workspace.DisposeAsync();

        // The container is gone, so anything addressed to it now fails. A leaked container per run would be
        // a slow disk leak that nothing else in the system would notice.
        await Assert.ThrowsAnyAsync<Exception>(() => workspace.RunCommandAsync(["echo", "hello"]));
    }

    /// <summary>
    /// Two properties of EnsureImageAsync that a run depends on, asserted together because they share
    /// one slow call. An image the daemon does not hold is still fetched, so making the presence check
    /// answer true for everything fails this. And a pull that cannot reach its registry is retried
    /// before it gives up, so deleting the retry fails this too, on the elapsed time.
    ///
    /// The registry is a name reserved by RFC 2606, so nothing here reaches a real one, and it fails
    /// fast enough that the elapsed time below is made of the waits and not of the failure.
    ///
    /// What this does NOT cover: that a present image skips the registry. That cannot be observed
    /// without a fake daemon, and is the half the comment on EnsureImageAsync has to carry instead.
    /// </summary>
    [Fact]
    public async Task An_unreachable_image_is_retried_then_fails_loudly_naming_itself()
    {
        using var docker = new DockerEngineClient(Options);
        var elapsed = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            docker.EnsureImageAsync("condux.invalid/nothing/here:0.0.0", CancellationToken.None));

        Assert.Contains("condux.invalid/nothing/here:0.0.0", failure.Message);

        // Both halves are needed, and the first is not decoration. Deriving the expected wait from
        // PullAttempts alone lets a plant that sets it to 1 move the goalposts to zero, which is exactly
        // what happened: the test passed against a build with the retry removed.
        Assert.True(DockerEngineClient.PullAttempts > 1, "one attempt is not a retry");

        var waits = DockerEngineClient.PullRetryDelay * (DockerEngineClient.PullAttempts - 1);
        Assert.True(elapsed.Elapsed >= waits, $"gave up after {elapsed.Elapsed}, expected at least {waits}");
    }

    /// <summary>The image the other tests use is present after an Ensure, whichever path it took.</summary>
    [Fact]
    public async Task Ensuring_the_test_image_leaves_it_usable()
    {
        using var docker = new DockerEngineClient(Options);

        await docker.EnsureImageAsync(Options.Image, CancellationToken.None);

        await using var workspace = await ContainerWorkspace.CreateAsync(docker, Options, Repo);
        var result = await RunAsync(workspace, "echo", "ok");
        Assert.Equal(0, result.ExitCode);
    }
}
