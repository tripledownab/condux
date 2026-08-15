namespace Condux.Sdk;

// The Sentry "store" wire shape the relay parses (event_id, timestamp, level, exception.values[]), so
// Condux normalizes a Condux SDK event exactly like an official Sentry SDK. Serialized with the snake_case
// naming policy (EventId → event_id, InApp → in_app), so these stay PascalCase.
internal sealed record EventPayload
{
    public required string EventId { get; init; }

    // Epoch seconds (the Sentry store convention), fractional.
    public required double Timestamp { get; init; }

    public required string Platform { get; init; }

    // Lowercase level string (the enum name lowercased).
    public required string Level { get; init; }

    public string? Environment { get; init; }

    public string? Release { get; init; }

    public string? ServerName { get; init; }

    public string? Message { get; init; }

    public ExceptionEnvelope? Exception { get; init; }

    // Ambient enrichment (ConduxScope). Null when unset and nulls are not written, so an event captured
    // without enrichment carries none of these four keys.
    public ConduxUser? User { get; init; }

    public IReadOnlyDictionary<string, string>? Tags { get; init; }

    // Dictionary keys are written verbatim (only property names take the naming policy), so a tag or a
    // context name reaches the relay exactly as the app spelled it.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? Contexts { get; init; }

    public BreadcrumbEnvelope? Breadcrumbs { get; init; }

    // Per-event, not ambient: the request this event happened during.
    public ConduxRequest? Request { get; init; }
}

/// <summary>
/// The request an event happened during. Property names take the serializer's snake_case policy, which
/// is what the relay's parser reads (url / method / query_string).
/// </summary>
/// <remarks>
/// Deliberately no headers: they carry cookies and authorization, and while the relay scrubs sensitive
/// keys at ingest, not sending credentials at all is the stronger guarantee.
/// </remarks>
public sealed record ConduxRequest
{
    public string? Url { get; init; }

    public string? Method { get; init; }

    public string? QueryString { get; init; }
}

// Breadcrumbs ride the Sentry {"values": []} envelope, not a bare array.
internal sealed record BreadcrumbEnvelope
{
    public required IReadOnlyList<Breadcrumb> Values { get; init; }
}

internal sealed record Breadcrumb
{
    public required string Message { get; init; }

    public string? Category { get; init; }

    // Lowercase level string, like the event's own level.
    public string? Level { get; init; }

    public string? Type { get; init; }

    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    // Epoch seconds (fractional), stamped at AddBreadcrumb when the caller omits it.
    public required double Timestamp { get; init; }
}

internal sealed record ExceptionEnvelope
{
    public required IReadOnlyList<SentryException> Values { get; init; }
}

internal sealed record SentryException
{
    public required string Type { get; init; }

    public required string Value { get; init; }

    // How the exception was captured (Sentry mechanism). A user-invoked CaptureException is a handled
    // capture, so type "generic" + handled true — this is what powers the "unhandled" badge.
    public Mechanism? Mechanism { get; init; }

    // Property name (→ wire key "stacktrace") is kept; the type is renamed to avoid a case-only clash
    // with System.Diagnostics.StackTrace.
    public SentryStacktrace? Stacktrace { get; init; }
}

internal sealed record Mechanism
{
    public required string Type { get; init; }

    public required bool Handled { get; init; }
}

internal sealed record SentryStacktrace
{
    public required IReadOnlyList<SentryFrame> Frames { get; init; }
}

internal sealed record SentryFrame
{
    // Absent (omitted) when no PDB is deployed, rather than a fabricated value; the declaring type rides
    // on Module instead so grouping still has a handle.
    public string? Filename { get; init; }

    public string? Module { get; init; }

    public required string Function { get; init; }

    public int Lineno { get; init; }

    public int Colno { get; init; }

    public required bool InApp { get; init; }
}
