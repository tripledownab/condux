using System.Diagnostics;
using System.Text;

namespace Condux.Symbolizer;

/// <summary>
/// Runs llvm-symbolizer over one debug file and a batch of addresses: one process per batch, so a file that
/// crashes or hangs the parser costs that batch and nothing else. Addresses go in on stdin and come back as
/// one JSON line each. The process is started from an argument list, never a shell, and killed with its
/// children when the timeout passes.
/// </summary>
internal sealed class LlvmSymbolizerRunner(SymbolizerOptions options, ILogger<LlvmSymbolizerRunner> logger)
{
    // What one batch may print. The names in the answer come from the file, so a file with one enormous
    // name answers every address with it; past this the batch fails instead of the service running out of
    // memory. The fixture's two-frame answer is 425 characters, so at that frame length a batch at the
    // address cap stays under this with inline chains up to about seventy frames deep.
    internal const int MaxOutputChars = 8 * 1024 * 1024;

    // How much of the parser's error output is kept for the log. The rest is read and dropped, since a
    // parser that cannot write its errors stops answering.
    private const int KeptErrorChars = 500;

    // A file that cannot be read is left for llvm-symbolizer to report, so it is not taken for Mach-O here.
    private static bool IsMachO(string debugFile)
    {
        Span<byte> head = stackalloc byte[4];
        try
        {
            using var stream = File.OpenRead(debugFile);
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                && Condux.Core.DebugFiles.MachODebugFile.LooksLikeMachO(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One JSON line per address, in the order sent, or null when the run failed or timed out.</summary>
    public async Task<IReadOnlyList<string>?> RunAsync(
        string debugFile, IReadOnlyList<ulong> addresses, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(options.LlvmSymbolizerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add($"--obj={debugFile}");
        start.ArgumentList.Add("--inlining");
        start.ArgumentList.Add("--output-style=JSON");
        // For a Mach-O file the default (linkage) name drops a leading underscore, as if it were the Darwin
        // symbol prefix, which turns a Dart private name such as _Cart.total into Cart.total. The short form
        // prints the DWARF name as written. ELF keeps the default, which also qualifies a C++ name.
        if (IsMachO(debugFile))
        {
            start.ArgumentList.Add("--functions=short");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ResolveTimeout);
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            process.Start();
            started = true;
            // Drained concurrently with the write, or a large batch fills the output pipe and both sides wait.
            var output = ReadLinesAsync(process.StandardOutput, timeout.Token);
            var errors = DrainAsync(process.StandardError, timeout.Token);
            foreach (var address in addresses)
            {
                await process.StandardInput.WriteLineAsync($"0x{address:x}".AsMemory(), timeout.Token);
            }
            process.StandardInput.Close();
            if (await output is not { } lines)
            {
                logger.LogWarning("llvm-symbolizer printed more than {Limit} characters for {Addresses} addresses",
                    MaxOutputChars, addresses.Count);
                return null;
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || lines.Count != addresses.Count)
            {
                logger.LogWarning("llvm-symbolizer exited {Code} with {Lines} lines for {Addresses} addresses: {Errors}",
                    process.ExitCode, lines.Count, addresses.Count, await errors);
                return null;
            }
            return lines;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning("llvm-symbolizer did not finish {Addresses} addresses in time", addresses.Count);
            return null;
        }
        catch (IOException exception)
        {
            // The parser died before it read every address, as one that crashes on a file does.
            logger.LogWarning(exception, "llvm-symbolizer closed its input early");
            return null;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            logger.LogError(exception, "llvm-symbolizer could not start from {Path}", options.LlvmSymbolizerPath);
            return null;
        }
        finally
        {
            if (started)
            {
                Kill(process);
            }
        }
    }

    // Read in chunks rather than by line, because a single line can be as long as the parser likes, and
    // reading it whole would spend the memory before the count could refuse it.
    private static async Task<List<string>?> ReadLinesAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        var buffer = new char[8192];
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxOutputChars)
            {
                return null;
            }
            foreach (var c in buffer.AsSpan(0, read))
            {
                if (c == '\n')
                {
                    AddLine(lines, line);
                }
                else if (c != '\r')
                {
                    line.Append(c);
                }
            }
        }
        AddLine(lines, line);
        return lines;
    }

    private static void AddLine(List<string> lines, StringBuilder line)
    {
        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }
        line.Clear();
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var kept = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            kept.Append(buffer, 0, Math.Min(read, KeptErrorChars - kept.Length));
        }
        return kept.ToString();
    }

    // Every path ends here, so no batch leaves a parser running. A process that already exited is the
    // normal case and throws nothing worth keeping.
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
            or AggregateException)
        {
            // It exited between the check and the kill, or a child could not be signalled. Thrown here, in
            // the finally, it would replace the batch's own answer.
        }
    }
}
