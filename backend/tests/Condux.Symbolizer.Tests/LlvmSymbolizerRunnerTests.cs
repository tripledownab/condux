using System.Diagnostics;
using Condux.Symbolizer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.Symbolizer.Tests;

/// <summary>
/// The runner's contract with a parser that misbehaves: a hang ends at the timeout and the process is
/// killed, a crash or a short answer is a failed batch, and a well-behaved run returns one line per
/// address. A real llvm-symbolizer cannot be made to do the first two on demand, so small shell scripts
/// stand in for it here; the real binary is driven by SymbolizerApiTest. They need a Unix shell.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LlvmSymbolizerRunnerTests : IDisposable
{
    private readonly FakeSymbolizers _fakes = new();

    public void Dispose() => _fakes.Dispose();

    private string FakeSymbolizer(string body) => _fakes.Write(body);

    private static LlvmSymbolizerRunner Runner(string path, TimeSpan timeout) =>
        new(new SymbolizerOptions(path, "/unused", 1, 512, 1, timeout), NullLogger<LlvmSymbolizerRunner>.Instance);

    [Fact]
    public async Task One_line_comes_back_per_address_in_order()
    {
        // Echoes each address it reads as a JSON line, the way the real parser answers.
        var path = FakeSymbolizer("""while read a; do echo "{\"Address\":\"$a\"}"; done""");

        var lines = await Runner(path, TimeSpan.FromSeconds(10)).RunAsync("/x", [0x10, 0x20], CancellationToken.None);

        Assert.Equal(["""{"Address":"0x10"}""", """{"Address":"0x20"}"""], lines);
    }

    [Fact]
    public async Task A_parser_that_hangs_is_stopped_at_the_timeout()
    {
        var path = FakeSymbolizer("exec sleep 300");
        var clock = Stopwatch.StartNew();

        var lines = await Runner(path, TimeSpan.FromMilliseconds(500)).RunAsync("/x", [0x10], CancellationToken.None);

        Assert.Null(lines);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_stopped_batch_is_killed_with_its_children()
    {
        // The fake starts a child that would outlive it, and records the child's process ID.
        var childPid = Path.Combine(_fakes.Directory, "child.pid");
        var path = FakeSymbolizer($"sleep 300 & echo $! > {childPid}.tmp; mv {childPid}.tmp {childPid}; wait");
        using var stop = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();

        // The batch is stopped only once the child exists, so a slow machine cannot end the run before
        // there is anything to kill. The stop arrives the way a timeout does, through the linked token.
        var run = Runner(path, TimeSpan.FromSeconds(60)).RunAsync("/x", [0x10], stop.Token);
        while (!File.Exists(childPid) && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
        }
        await stop.CancelAsync();
        var lines = await run;

        Assert.True(File.Exists(childPid), "the fake never started its child");
        Assert.Null(lines);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        var pid = int.Parse(File.ReadAllText(childPid).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await HasEndedAsync(pid), $"child {pid} outlived the batch");
    }

    // Waits for a condition rather than a fixed time: a killed process takes a moment to go, and one
    // nobody killed never does, so the bound only decides how long a failure takes to report.
    private static async Task<bool> HasEndedAsync(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var probe = Process.Start(new ProcessStartInfo("kill", ["-0", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)])
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            await probe.WaitForExitAsync();
            if (probe.ExitCode != 0)
            {
                return true;
            }
            await Task.Yield();
        }
        return false;
    }

    [Fact]
    public async Task A_parser_that_crashes_fails_the_batch()
    {
        var path = FakeSymbolizer("read a; echo '{}'; exit 3");

        Assert.Null(await Runner(path, TimeSpan.FromSeconds(10)).RunAsync("/x", [0x10], CancellationToken.None));
    }

    [Fact]
    public async Task A_short_answer_fails_the_batch_rather_than_misaligning_it()
    {
        var path = FakeSymbolizer("""read a; echo "{\"Address\":\"$a\"}"; cat > /dev/null""");

        Assert.Null(await Runner(path, TimeSpan.FromSeconds(10)).RunAsync("/x", [0x10, 0x20], CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_parser_fails_the_batch_instead_of_throwing()
    {
        var runner = Runner(Path.Combine(_fakes.Directory, "absent"), TimeSpan.FromSeconds(10));

        Assert.Null(await runner.RunAsync("/x", [0x10], CancellationToken.None));
    }

    [Fact]
    public async Task An_answer_past_the_output_cap_fails_the_batch()
    {
        // One line longer than the cap, as a file with an enormous name would produce.
        var path = FakeSymbolizer(
            $"read a; head -c {LlvmSymbolizerRunner.MaxOutputChars + 1} /dev/zero | tr '\\0' a; echo");

        var lines = await Runner(path, TimeSpan.FromSeconds(30)).RunAsync("/x", [0x10], CancellationToken.None);

        Assert.Null(lines);
    }

    [Fact]
    public async Task Error_output_past_what_is_kept_is_still_read_so_the_answer_arrives()
    {
        // Far more than a pipe holds, so a reader that stopped draining would leave the parser blocked.
        var path = FakeSymbolizer(
            """head -c 4000000 /dev/zero >&2; while read a; do echo "{\"Address\":\"$a\"}"; done""");

        var lines = await Runner(path, TimeSpan.FromSeconds(30)).RunAsync("/x", [0x10], CancellationToken.None);

        Assert.Equal(["""{"Address":"0x10"}"""], lines);
    }
}
