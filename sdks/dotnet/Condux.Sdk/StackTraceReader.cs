using System.Diagnostics;
using System.Reflection;

namespace Condux.Sdk;

// Turns an Exception into the Sentry exception shape: type, message, and structured frames.
internal static class StackTraceReader
{
    public static SentryException ToException(Exception error, bool handled = true)
    {
        var frames = ReadFrames(error);
        return new SentryException
        {
            Type = error.GetType().FullName ?? error.GetType().Name,
            Value = error.Message,
            // A direct CaptureException is a handled capture; a framework integration reporting an uncaught
            // exception passes handled = false. The relay reads mechanism.handled for the unhandled badge.
            Mechanism = new Mechanism { Type = "generic", Handled = handled },
            Stacktrace = frames.Count > 0 ? new SentryStacktrace { Frames = frames } : null,
        };
    }

    // .NET frames are innermost-first (the throw site first); reverse to oldest-first (crashing frame
    // last), the order the relay's fingerprinter + issue detail expect (matches the JS/Python SDKs).
    // File name + line number are present only when a PDB is deployed; absent, the frame keeps its
    // function + module.
    private static IReadOnlyList<SentryFrame> ReadFrames(Exception error)
    {
        var trace = new StackTrace(error, fNeedFileInfo: true);
        var frames = new List<SentryFrame>();
        foreach (var frame in trace.GetFrames())
        {
            if (frame.GetMethod() is not { } method)
            {
                continue;
            }

            frames.Add(new SentryFrame
            {
                Filename = frame.GetFileName(),
                Module = method.DeclaringType?.FullName,
                Function = FunctionName(method),
                Lineno = frame.GetFileLineNumber(),
                Colno = frame.GetFileColumnNumber(),
                InApp = IsInApp(method),
            });
        }

        frames.Reverse();
        return frames;
    }

    private static string FunctionName(MethodBase method) =>
        method.DeclaringType is { } type ? $"{type.FullName}.{method.Name}" : method.Name;

    // Application frames drive grouping + the culprit; the BCL and framework are noise. Excludes the
    // System.* and Microsoft.* namespaces (the Sentry .NET default), keyed on the declaring type.
    private static bool IsInApp(MethodBase method)
    {
        var declaringNamespace = method.DeclaringType?.Namespace;
        if (declaringNamespace is null)
        {
            return true;
        }

        return !declaringNamespace.StartsWith("System", StringComparison.Ordinal)
            && !declaringNamespace.StartsWith("Microsoft", StringComparison.Ordinal);
    }
}
