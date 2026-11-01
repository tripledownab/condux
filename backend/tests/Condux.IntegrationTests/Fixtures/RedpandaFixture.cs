using System.Text;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral single-node Redpanda container, on the image <see cref="ComposeImages"/> reads from the
/// compose file, so a compose bump reaches this fixture with no second place to edit.
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
/// after it (4.16 also waits for the bindings to be mapped before the callback). Read out of
/// DockerContainer.UnsafeStartAsync, so a version bump that reorders it breaks this fixture, and loudly:
/// reading a port before it is mapped throws. The Testcontainers Redpanda module drives its own container the
/// same way and advertises the container Hostname, which is the one thing done differently here.
///
/// It advertises 127.0.0.1 rather than that Hostname deliberately: the name resolves to ::1 first on
/// this host, while Docker binds IPv4 only, so a client following the advertised address connects to
/// nothing.
///
/// The image entrypoint is bypassed, which costs nothing. Without ENABLE_DEFAULT_LISTENERS, which
/// nothing here sets, /entrypoint.sh is exec /usr/bin/rpk "$@" and no more.
///
/// The broker's memory is bounded twice, and both bounds are load-bearing. Dev-container mode passes
/// --reserve-memory=0M and no --memory, so Seastar budgets all the memory it can see: a broker took
/// about 5.6 GB, every class using this fixture starts its own, and the suite runs classes in parallel.
/// Several suites on one machine exhausted it and the kernel began killing unrelated processes. --memory
/// sets Seastar's budget outright, and the container limit sits above it for what Seastar does not
/// allocate, with no swap behind it, so a broker that outgrows both is killed alone.
/// RedpandaMemoryBoundsTest.cs holds the budget, the limit and the swap setting.
/// </summary>
public sealed class RedpandaFixture : IAsyncLifetime
{
    private const int BrokerPort = 9092;
    internal const string MemoryBudget = "512M";
    internal const long ContainerMemoryLimitBytes = 768L * 1024 * 1024;
    private const string StartScript = "/condux-start-redpanda.sh";

    public IContainer Container { get; } = new ContainerBuilder(ComposeImages.For("redpandadata/redpanda"))
        .WithPortBinding(BrokerPort, true)
        .WithCreateParameterModifier(parameters =>
        {
            parameters.HostConfig ??= new HostConfig();
            parameters.HostConfig.Memory = ContainerMemoryLimitBytes;
            parameters.HostConfig.MemorySwap = ContainerMemoryLimitBytes;
        })
        .WithEntrypoint("/bin/sh", "-c")
        .WithCommand($"while [ ! -f {StartScript} ]; do sleep 0.1; done; exec {StartScript}")
        .WithStartupCallback((container, ct) => container.CopyAsync(
            Encoding.ASCII.GetBytes(StartCommand(container.GetMappedPublicPort(BrokerPort))),
            StartScript,
            fileMode: Unix.FileMode755,
            ct: ct))
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(BrokerPort))
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
          --memory {MemoryBudget} \
          --default-log-level=warn \
          --kafka-addr external://0.0.0.0:{BrokerPort} \
          --advertise-kafka-addr external://127.0.0.1:{hostPort}
        """;
}
