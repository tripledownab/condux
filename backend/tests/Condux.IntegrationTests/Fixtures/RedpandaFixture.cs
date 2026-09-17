using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral single-node Redpanda container, on the image tag the compose stack also runs
/// (arm64-native v24.2.7; nothing compares the two, so a compose bump has to be repeated here).
/// Requires Confluent.Kafka 2.5.x (2.6.0 requests Fetch API v12, which Redpanda rejects).
///
/// The host port is assigned by Docker, never chosen here. A Kafka broker has to advertise the address
/// clients reconnect to, and that address is not known until the port is mapped, so this file used to
/// pin one instead. A pinned host port is not a workaround for that. It is a collision with whatever
/// else on the machine wants the port, and it surfaces as a failure in whichever change happens to be
/// running rather than in the one that caused it.
///
/// The way out is to defer the broker's own start until the mapping exists: the container comes up on
/// a shell that waits for a script, the startup callback reads the mapped port and writes that script,
/// and the broker starts knowing what to advertise. That relies on Testcontainers' own order, which
/// maps the bindings and starts the container BEFORE invoking the callback, and runs the wait strategy
/// after it. Read out of DockerContainer.UnsafeStartAsync at the 4.1.0 the csproj pins, so a version
/// bump is what would invalidate it. The Testcontainers Redpanda module drives its own container the
/// same way and advertises the container Hostname, which is the one thing done differently here.
///
/// It advertises 127.0.0.1 rather than that Hostname deliberately: the name resolves to ::1 first on
/// this host, while Docker binds IPv4 only, so a client following the advertised address connects to
/// nothing.
///
/// The image entrypoint is bypassed, which costs nothing. Without ENABLE_DEFAULT_LISTENERS, which
/// nothing here sets, /entrypoint.sh is exec /usr/bin/rpk "$@" and no more.
/// </summary>
public sealed class RedpandaFixture : IAsyncLifetime
{
    private const int BrokerPort = 9092;
    private const string StartScript = "/condux-start-redpanda.sh";

    public IContainer Container { get; } = new ContainerBuilder()
        .WithImage("redpandadata/redpanda:v24.2.7")
        .WithPortBinding(BrokerPort, true)
        .WithEntrypoint("/bin/sh", "-c")
        .WithCommand($"while [ ! -f {StartScript} ]; do sleep 0.1; done; exec {StartScript}")
        .WithStartupCallback((container, ct) => container.CopyAsync(
            Encoding.ASCII.GetBytes(StartCommand(container.GetMappedPublicPort(BrokerPort))),
            StartScript,
            Unix.FileMode755,
            ct))
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(BrokerPort))
        .Build();

    public string BootstrapAddress => $"127.0.0.1:{Container.GetMappedPublicPort(BrokerPort)}";

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    private static string StartCommand(ushort hostPort) =>
        $"""
        #!/bin/sh
        exec /usr/bin/rpk redpanda start \
          --mode dev-container \
          --smp 1 \
          --default-log-level=warn \
          --kafka-addr external://0.0.0.0:{BrokerPort} \
          --advertise-kafka-addr external://127.0.0.1:{hostPort}
        """;
}
