using System.Globalization;
using System.Text.RegularExpressions;
using Condux.Core.Events;

namespace Condux.Relay.Ingest;

/// <summary>
/// Reads frames out of the string an OTLP <c>exception.stacktrace</c> attribute carries.
/// </summary>
/// <remarks>
/// The protocol says only that the attribute holds a stacktrace, not what shape it has, so this is a
/// per-runtime judgement rather than protocol handling. A stack from any runtime this cannot read yields
/// no frames, and the exception's type and message still group the issue.
/// </remarks>
internal static class OtlpStacktrace
{
    // A V8 `error.stack` line: "at fn (file:line:col)" or the anonymous "at file:line:col". Non-matching
    // lines, including the leading "Type: message", are skipped.
    //
    // NonBacktracking is load-bearing, and RegexOptions.Compiled here was not enough. This string is an
    // OTLP attribute the sender chooses, bounded only by CONDUX_MAX_INGEST_BYTES, and the two lazy groups
    // are quadratic: every " (" in the line is a candidate function-name boundary, and each one rescans
    // the rest looking for a ":digits:digits" tail. Measured compiled, a single line of " (" repeated
    // takes 76ms at 30,000 repetitions and 14.2s at 480,000: a 16-fold longer line for about 190 times
    // the work, on one ingest thread, for a request under a megabyte. Linear it is about a millisecond.
    //
    // The lazy quantifiers are kept because a file can contain colons (`https://…`, a Windows path), so a
    // negated class here would stop reading real stacks.
    private static readonly Regex V8Frame = new(
        @"^\s*at (?:(?<fn>.+?) \()?(?<file>.+?):(?<line>\d+):(?<col>\d+)\)?\s*$",
        RegexOptions.NonBacktracking);

    // A real stack is dozens of frames. Reading a bounded prefix keeps a 20 MiB attribute of newlines from
    // costing a list of millions, and the cap is on frames kept as well as lines read because the two
    // diverge: a stack can be all frames or none.
    private const int MaxLines = 1000;
    private const int MaxFrames = 250;

    internal static Stacktrace? ParseV8(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return null;
        }

        var frames = new List<Frame>();
        var lines = 0;
        foreach (var line in stack.AsSpan().EnumerateLines())
        {
            if (++lines > MaxLines || frames.Count == MaxFrames)
            {
                break;
            }

            var match = V8Frame.Match(line.ToString());
            if (!match.Success)
            {
                continue;
            }

            var filename = match.Groups["file"].Value;
            frames.Add(new Frame
            {
                Filename = filename,
                Function = match.Groups["fn"].Success ? match.Groups["fn"].Value : "<anonymous>",
                Lineno = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                Colno = int.Parse(match.Groups["col"].Value, CultureInfo.InvariantCulture),
                InApp = IsInApp(filename),
            });
        }

        if (frames.Count == 0)
        {
            return null;
        }

        // V8 stacks are newest-first; the model orders oldest (outermost) to newest (crashing). Reversing
        // after the cap is also why the cap keeps the newest frames, which are the ones that name the fault.
        frames.Reverse();
        return new Stacktrace { Frames = frames };
    }

    /// <summary>
    /// Whether a frame is the application's own code rather than a dependency or the runtime.
    /// </summary>
    /// <remarks>
    /// This answers the same question as the JS SDK's own <c>isInApp</c>, for the same V8 frames, and the
    /// two must agree: the fingerprint is built from every in-app frame, so one path calling a frame
    /// application code while the other does not splits a single fault into two issues depending on how
    /// it was reported.
    ///
    /// <b>unenforced parity:</b> the other side is TypeScript, in <c>sdks/core/src/stack.ts</c>, so no
    /// test in this solution can run both and compare. Keep this list and the SDK's in step whenever
    /// either moves. The framework entry is not decoration: a bundled
    /// Next.js server puts its own frames in every stack, and marking those in-app puts them in the
    /// fingerprint, where a framework upgrade then re-groups every existing issue.
    /// </remarks>
    private static bool IsInApp(string filename)
        => !filename.Contains("node_modules", StringComparison.Ordinal)
            && !filename.StartsWith("node:", StringComparison.Ordinal)
            && !filename.Contains("next/dist/", StringComparison.Ordinal);
}
