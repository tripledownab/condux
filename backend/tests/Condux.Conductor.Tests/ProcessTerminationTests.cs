using Condux.Telemetry;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The crash handler that makes a failed service actually stop.
///
/// The termination itself only misbehaves at PID 1 (the kernel discards a default-disposition signal sent
/// to it, so .NET's abort leaves the process spinning) and that is verified by running the images. What is
/// testable here is the handler's own behaviour, and that is where the real bug was: the first version
/// exited without writing anything, because this handler runs BEFORE the runtime reports the exception.
/// The container died with an empty log, which is only marginally better than not dying at all.
/// </summary>
public class ProcessTerminationTests
{
    [Fact]
    public void An_unhandled_exception_is_reported_and_exits_non_zero()
    {
        var exitCodes = new List<int>();
        var originalExit = ProcessTermination.Exit;
        var originalError = Console.Error;
        var captured = new StringWriter();

        try
        {
            ProcessTermination.Exit = exitCodes.Add;
            Console.SetError(captured);

            ProcessTermination.OnUnhandled(
                sender: null,
                new UnhandledExceptionEventArgs(
                    new InvalidOperationException("the database is not configured"), isTerminating: true));
        }
        finally
        {
            ProcessTermination.Exit = originalExit;
            Console.SetError(originalError);
        }

        // Without this an operator gets a container that exited 1 and no reason anywhere.
        Assert.Contains("the database is not configured", captured.ToString(), StringComparison.Ordinal);

        // Non-zero, or a supervisor reads the crash as a clean shutdown and neither restarts nor alerts.
        Assert.Equal([1], exitCodes);
    }
}
