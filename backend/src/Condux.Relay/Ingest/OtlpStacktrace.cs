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
    private static readonly Regex V8Frame = new(
        @"^\s*at (?:(?<fn>.+?) \()?(?<file>.+?):(?<line>\d+):(?<col>\d+)\)?\s*$",
        RegexOptions.Compiled);

    internal static Stacktrace? ParseV8(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return null;
        }

        var frames = new List<Frame>();
        foreach (var line in stack.Split('\n'))
        {
            var match = V8Frame.Match(line);
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

        // V8 stacks are newest-first; the model orders oldest (outermost) to newest (crashing).
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
    /// it was reported. They cannot share an implementation across the language boundary, so keep this
    /// list and the SDK's in step whenever either moves. The framework entry is not decoration: a bundled
    /// Next.js server puts its own frames in every stack, and marking those in-app puts them in the
    /// fingerprint, where a framework upgrade then re-groups every existing issue.
    /// </remarks>
    private static bool IsInApp(string filename)
        => !filename.Contains("node_modules", StringComparison.Ordinal)
            && !filename.StartsWith("node:", StringComparison.Ordinal)
            && !filename.Contains("next/dist/", StringComparison.Ordinal);
}
