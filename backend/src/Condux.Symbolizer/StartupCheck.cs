using System.Diagnostics;

namespace Condux.Symbolizer;

/// <summary>
/// Refuses to start without a working llvm-symbolizer. Without this the service would answer ready and fail
/// every resolve, which looks like hostile input rather than a broken install.
/// </summary>
internal static class StartupCheck
{
    public static async Task RequireSymbolizerAsync(SymbolizerOptions options)
    {
        var start = new ProcessStartInfo(options.LlvmSymbolizerPath, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {options.LlvmSymbolizerPath}");
        // Bounded, so a binary that hangs fails the start rather than holding it open for ever.
        using var timeout = new CancellationTokenSource(options.ResolveTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"{options.LlvmSymbolizerPath} --version did not finish; set CONDUX_LLVM_SYMBOLIZER");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{options.LlvmSymbolizerPath} --version exited {process.ExitCode}; set CONDUX_LLVM_SYMBOLIZER");
        }
    }
}
