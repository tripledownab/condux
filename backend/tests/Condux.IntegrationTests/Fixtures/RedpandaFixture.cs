using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral single-node Redpanda container — the same broker + config the compose
/// stack runs (arm64-native v24.2.7). A plain container with an explicit
/// <c>--advertise-kafka-addr</c> and a fixed host port; the Testcontainers Redpanda
/// module was unreliable here (its listener proxy dropped fetches). Advertises
/// 127.0.0.1 to avoid the host's IPv6 <c>localhost</c> resolving ahead of Docker's
/// IPv4-only port binding. Requires Confluent.Kafka 2.5.x (2.6.0 requests Fetch API
/// v12, which Redpanda rejects).
/// </summary>
public sealed class RedpandaFixture : IAsyncLifetime
{
    private const int HostPort = 39092;

    public IContainer Container { get; } = new ContainerBuilder()
        .WithImage("redpandadata/redpanda:v24.2.7")
        .WithPortBinding(HostPort, 9092)
        .WithCommand(
            "redpanda", "start",
            "--mode", "dev-container",
            "--smp", "1",
            "--default-log-level=warn",
            "--kafka-addr", "external://0.0.0.0:9092",
            "--advertise-kafka-addr", $"external://127.0.0.1:{HostPort}")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(9092))
        .Build();

    public string BootstrapAddress => $"127.0.0.1:{HostPort}";

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
