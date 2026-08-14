using Condux.Agent.Sandbox;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.Conductor.Tests;

public class SandboxOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void The_sandbox_is_off_when_no_image_is_configured()
    {
        // Off is the default: with nothing to run a command in, the agent falls back to a workspace that
        // reports it cannot execute, rather than failing a run that would otherwise have succeeded.
        Assert.Null(SandboxOptions.FromEnv(Config()));
    }

    [Fact]
    public void An_image_alone_falls_back_to_the_local_docker_socket()
    {
        var options = SandboxOptions.FromEnv(Config(("CONDUX_SANDBOX_IMAGE", "condux/sandbox:1")));

        Assert.NotNull(options);
        Assert.True(options.IsUnixSocket);
        Assert.Equal("/var/run/docker.sock", options.SocketPath);
    }

    [Fact]
    public void A_tcp_endpoint_points_the_runtime_at_another_host()
    {
        // The reason the endpoint is configurable at all: a deployment can keep the container runtime off
        // the machine holding the datastores.
        var options = SandboxOptions.FromEnv(Config(
            ("CONDUX_SANDBOX_IMAGE", "condux/sandbox:1"),
            ("CONDUX_SANDBOX_DOCKER_ENDPOINT", "tcp://sandbox.internal:2376")));

        Assert.NotNull(options);
        Assert.False(options.IsUnixSocket);
        Assert.Equal("sandbox.internal", options.Endpoint.Host);
    }

    [Theory]
    [InlineData("ftp://nope")]
    [InlineData("not-a-uri")]
    public void An_endpoint_that_is_not_a_docker_endpoint_fails_fast(string endpoint)
    {
        Assert.Throws<InvalidOperationException>(() => SandboxOptions.FromEnv(Config(
            ("CONDUX_SANDBOX_IMAGE", "condux/sandbox:1"),
            ("CONDUX_SANDBOX_DOCKER_ENDPOINT", endpoint))));
    }

    [Theory]
    [InlineData("CONDUX_SANDBOX_MEMORY_MB")]
    [InlineData("CONDUX_SANDBOX_PIDS")]
    [InlineData("CONDUX_SANDBOX_TIMEOUT_SECONDS")]
    public void A_limit_that_is_not_a_positive_number_fails_fast(string key)
    {
        // A misread limit is worse than an unset one: zero or a negative would silently mean "no cap" to
        // some of these, which is the opposite of what someone typing a limit intended.
        Assert.Throws<InvalidOperationException>(() => SandboxOptions.FromEnv(Config(
            ("CONDUX_SANDBOX_IMAGE", "condux/sandbox:1"), (key, "0"))));
    }

    [Fact]
    public void Limits_are_read_in_the_units_they_are_named_in()
    {
        var options = SandboxOptions.FromEnv(Config(
            ("CONDUX_SANDBOX_IMAGE", "condux/sandbox:1"),
            ("CONDUX_SANDBOX_MEMORY_MB", "512"),
            ("CONDUX_SANDBOX_TIMEOUT_SECONDS", "90")));

        Assert.NotNull(options);
        Assert.Equal(512L * 1024 * 1024, options.MemoryBytes);
        Assert.Equal(90, options.CommandTimeout.TotalSeconds);
    }
}
