namespace Condux.Core.Events;

/// <summary>
/// Which of an event's exceptions is the one that crashed. The Sentry wire shape orders
/// <c>exception.values[]</c> oldest first, so the last entry is the throw the SDK actually caught and
/// every earlier one is a cause it wrapped.
///
/// This is one line, and it lived as four copies of that line: the fingerprint, the issue title, the
/// stored ClickHouse row and the fix context each picked <c>Exceptions[^1]</c> for themselves, across
/// two projects. Changing it in one of them left the issue a reader sees grouped on one exception and
/// titled after another. It has one home now.
/// </summary>
public static class EventExceptions
{
    /// <summary>The crashing exception, or null when the event carries none.</summary>
    public static ExceptionValue? ResolvePrimary(Event e) =>
        e.Exceptions.Count > 0 ? e.Exceptions[^1] : null;
}
