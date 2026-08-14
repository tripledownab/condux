namespace Condux.Telemetry;

/// <summary>
/// Makes an unhandled exception actually end the process.
///
/// It does not, by default, in a container. .NET reports the exception and then calls abort, which raises
/// SIGABRT at itself — and the kernel discards a signal sent to PID 1 when PID 1 has no handler for it.
/// Our services are PID 1 in their images, so the process survives its own crash: it prints a stack trace,
/// spins at full CPU and reports `running=true, exit=0` forever. Nothing restarts it and no health check
/// notices, because from the outside it never failed.
///
/// That quietly voids the fail-fast the services are built around. A missing <c>CONDUX_POSTGRES</c> is
/// meant to be loud and immediate; instead it produced a container that looked healthy and did nothing.
///
/// <see cref="Environment.Exit(int)"/> terminates by request rather than by signal, so PID 1 has no say in
/// it. Registered explicitly by each service rather than folded into another call, because a process that
/// dies when it should is worth seeing in the composition root.
/// </summary>
public static class ProcessTermination
{
    /// <summary>Overridable so the handler's behaviour can be asserted without ending the test run.</summary>
    internal static Action<int> Exit { get; set; } = Environment.Exit;

    /// <summary>Non-zero, so a supervisor treats it as the failure it is and restarts or alerts.</summary>
    internal const int FailureCode = 1;

    /// <summary>
    /// Exit non-zero when an exception reaches the top of any thread. Call it first in a service's
    /// composition root: everything after it, configuration reads included, is then covered.
    /// </summary>
    public static void ExitOnUnhandledException() =>
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

    internal static void OnUnhandled(object? sender, UnhandledExceptionEventArgs args)
    {
        // Written here, not left to the runtime. This handler runs BEFORE the runtime reports the
        // exception, so exiting from it pre-empts that report and the container dies with an empty log —
        // which trades a process that never stops for one that stops without saying why. Verified by
        // running the image: without this line the exit code is 1 and `docker logs` is blank.
        Console.Error.WriteLine($"Unhandled exception. {args.ExceptionObject}");
        Console.Error.Flush();
        Exit(FailureCode);
    }
}
