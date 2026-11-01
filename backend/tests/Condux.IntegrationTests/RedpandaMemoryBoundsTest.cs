// Holds the memory bounds on the test broker. Each is read from inside the running container and
// compared with the fixture's constant, so a builder change that drops one fails here. A partial of
// MessagingRoundtripTest so it reads that class's broker rather than starting one of its own.
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

public partial class MessagingRoundtripTest
{
    [Fact]
    public async Task The_broker_container_runs_under_the_memory_limit_the_fixture_sets()
    {
        var limit = await ReadFromBrokerAsync(
            "cat /sys/fs/cgroup/memory.max 2>/dev/null || cat /sys/fs/cgroup/memory/memory.limit_in_bytes");

        Assert.Equal(RedpandaFixture.ContainerMemoryLimitBytes.ToString(), limit.Trim());
    }

    [Fact]
    public async Task The_broker_container_may_not_swap()
    {
        // Docker allows as much swap again as the memory limit unless MemorySwap matches it.
        var swap = await ReadFromBrokerAsync(
            "if [ -f /sys/fs/cgroup/memory.swap.max ]; then cat /sys/fs/cgroup/memory.swap.max; " +
            "else echo $(( $(cat /sys/fs/cgroup/memory/memory.memsw.limit_in_bytes) - $(cat /sys/fs/cgroup/memory/memory.limit_in_bytes) )); fi");

        Assert.Equal("0", swap.Trim());
    }

    [Fact]
    public async Task The_broker_runs_with_a_memory_budget()
    {
        // The broker is what rpk exec'd, so its own command line is the one Seastar parsed.
        var commandLines = await ReadFromBrokerAsync("for f in /proc/[0-9]*/cmdline; do tr '\\0' ' ' < \"$f\"; echo; done");
        var broker = commandLines.Split('\n').Single(line => line.StartsWith("/opt/redpanda/bin/redpanda ", StringComparison.Ordinal));

        Assert.Contains($"--memory={RedpandaFixture.MemoryBudget}", broker, StringComparison.Ordinal);
    }

    private async Task<string> ReadFromBrokerAsync(string script)
    {
        var result = await redpanda.Container.ExecAsync(["/bin/sh", "-c", script]);
        Assert.Equal(0, result.ExitCode);
        return result.Stdout;
    }
}
