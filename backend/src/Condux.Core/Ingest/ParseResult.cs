using Condux.Core.Events;

namespace Condux.Core.Ingest;

/// <summary>How parsing an SDK payload ended.</summary>
public enum ParseOutcome
{
    /// <summary>An event was found and parsed.</summary>
    Parsed,

    /// <summary>Well formed, but carried nothing an error monitor stores: an envelope of only
    /// sessions, transactions or client reports. A legitimate 2xx, not a failure.</summary>
    NoEvent,

    /// <summary>Could not be parsed at all. Must not be reported to the SDK as success: doing so
    /// loses the event silently, with no signal on either side.</summary>
    Malformed,
}

/// <summary>
/// The outcome of a parse plus the event when there is one. The outcome is explicit because
/// "nothing to store" and "this payload is broken" are different answers to the caller, and
/// collapsing them into a null event reports a failure as a success.
/// </summary>
public readonly record struct ParseResult(ParseOutcome Outcome, Event? Event)
{
    public static ParseResult Parsed(Event parsed) => new(ParseOutcome.Parsed, parsed);

    public static ParseResult NoEvent { get; } = new(ParseOutcome.NoEvent, null);

    public static ParseResult Malformed { get; } = new(ParseOutcome.Malformed, null);
}
