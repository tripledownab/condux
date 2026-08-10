namespace Condux.Core.Events;

/// <summary>Severity of an event, mirroring the common SDK levels.</summary>
public enum Level
{
    Unspecified,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>A single stack frame.</summary>
public sealed record Frame
{
    public string? Filename { get; init; }

    /// <summary>The frame's absolute path or URL (Sentry <c>abs_path</c>). Kept for source-map matching by
    /// path when a frame carries no debug id (ADR-0028); a bundled web SDK usually sends the built file URL
    /// here (e.g. https://app/js/app.min.js).</summary>
    public string? AbsPath { get; init; }
    public string? Function { get; init; }
    public string? Module { get; init; }
    public int Lineno { get; init; }
    public int Colno { get; init; }
    public bool InApp { get; init; }
    public string? ContextLine { get; init; }

    /// <summary>Source lines around <see cref="ContextLine"/> (Sentry pre_context/post_context),
    /// powering the code-path view. Scrubbed like any string; empty when the SDK sends none.</summary>
    public IReadOnlyList<string> ContextBefore { get; init; } = [];
    public IReadOnlyList<string> ContextAfter { get; init; } = [];

    /// <summary>Local variable values at this frame (Sentry vars), stringified. Scrubbed by key and
    /// value; empty when the SDK does not capture locals.</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    /// <summary>The package or binary image the frame belongs to (Sentry <c>package</c>): the Dart package
    /// for a Dart frame, the loaded binary for a native one.</summary>
    public string? Package { get; init; }

    /// <summary>The resolved symbol name when the SDK sends one separately from <see cref="Function"/>.</summary>
    public string? Symbol { get; init; }

    /// <summary>Native symbolication inputs, sent by the mobile SDKs for frames with no source-level
    /// information. Kept verbatim and unscrubbed (opaque addresses, like a debug id) because they are
    /// discarded irrecoverably otherwise: an event stored without them can never be symbolicated later,
    /// even once a symbolication path exists. Null for a source-level frame.</summary>
    public string? InstructionAddr { get; init; }
    public string? ImageAddr { get; init; }
    public string? SymbolAddr { get; init; }
}

public sealed record Stacktrace
{
    /// <summary>Ordered oldest (outermost) → newest (crashing) frame.</summary>
    public IReadOnlyList<Frame> Frames { get; init; } = [];
}

/// <summary>One exception in a (possibly chained) error.</summary>
public sealed record ExceptionValue
{
    public string? Type { get; init; }
    public string? Value { get; init; }
    public string? Module { get; init; }
    public Stacktrace? Stacktrace { get; init; }

    /// <summary>Whether the error was caught by the app (from the SDK's mechanism); null = unreported.
    /// False is the "unhandled crash" badge.</summary>
    public bool? Handled { get; init; }
}

/// <summary>The user affected by an event. Privacy-first at rest: the scrubber redacts
/// <see cref="Email"/>, drops <see cref="IpAddress"/> entirely, and derives the pseudonymous
/// <see cref="Event.UserKey"/> for distinct "users affected" counts before either happens.</summary>
public sealed record EventUser
{
    public string? Id { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }
    public string? IpAddress { get; init; }
}

/// <summary>The HTTP request that produced the error (url/method/query plus headers, all scrubbed —
/// sensitive header keys are redacted).</summary>
public sealed record RequestInfo
{
    public string? Url { get; init; }
    public string? Method { get; init; }
    public string? QueryString { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>A breadcrumb — a trail of what happened before the error.</summary>
public sealed record Breadcrumb
{
    public long TimestampUnixMs { get; init; }
    public string? Type { get; init; }
    public string? Category { get; init; }
    public string? Message { get; init; }
    public Level Level { get; init; }

    /// <summary>Structured crumb payload (an http crumb's url/status, a ui crumb's selector...).</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

/// <summary>A debug image (Sentry <c>debug_meta.images[]</c>): links a debug id to the built file it
/// identifies, so a stack frame can be matched to its uploaded source map for symbolication (ADR-0028).
/// For a JS source map the <see cref="Type"/> is "sourcemap" and <see cref="CodeFile"/> is the built
/// file's URL.</summary>
public sealed record DebugImage
{
    public string? Type { get; init; }
    public string? CodeFile { get; init; }
    public string? DebugId { get; init; }
}

/// <summary>The normalized error event the relay produces and the consumer stores.</summary>
public sealed record Event
{
    public string EventId { get; init; } = "";
    public long TimestampUnixMs { get; init; }
    public string? Platform { get; init; }
    public Level Level { get; init; }
    public string? Logger { get; init; }
    public string? ServerName { get; init; }
    public string? Release { get; init; }
    public string? Environment { get; init; }
    public string? Transaction { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<ExceptionValue> Exceptions { get; init; } = [];
    public IReadOnlyList<Breadcrumb> Breadcrumbs { get; init; } = [];
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Fingerprint { get; init; } = [];
    public string? SdkName { get; init; }
    public string? SdkVersion { get; init; }

    /// <summary>The affected user as captured (scrubbed before storage — see <see cref="EventUser"/>).</summary>
    public EventUser? User { get; init; }

    /// <summary>Stable pseudonymous key for distinct "users affected" counts: a hash of the strongest
    /// user identifier, derived by the scrubber at ingest. Empty when the event carried no user.</summary>
    public string UserKey { get; init; } = "";

    /// <summary>The HTTP request that produced the error, when the SDK sent one.</summary>
    public RequestInfo? Request { get; init; }

    /// <summary>Flattened runtime contexts ("browser" → "Chrome 126", "os", "device", "runtime") — the
    /// facet-friendly form of Sentry's contexts object.</summary>
    public IReadOnlyDictionary<string, string> Contexts { get; init; } = new Dictionary<string, string>();

    /// <summary>Release sub-distribution (Sentry <c>dist</c>).</summary>
    public string? Dist { get; init; }

    /// <summary>Installed dependency versions (Sentry <c>modules</c>) — "which library version broke".</summary>
    public IReadOnlyDictionary<string, string> Modules { get; init; } = new Dictionary<string, string>();

    /// <summary>Debug images (Sentry <c>debug_meta.images[]</c>) mapping a debug id to its built file, for
    /// source-map symbolication (ADR-0028). Empty when the SDK sent none.</summary>
    public IReadOnlyList<DebugImage> DebugImages { get; init; } = [];

    /// <summary>The distributed trace this event belongs to (contexts.trace.trace_id), for APM linking.</summary>
    public string? TraceId { get; init; }
}
