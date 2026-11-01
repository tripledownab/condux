using Condux.Core.Events;

namespace Condux.Core.Tickets;

/// <summary>
/// When a ticket opens without anyone asking (ADR-0053 slice 2): the project turned it on, the issue is new
/// rather than a later occurrence or a regression, and it is at least an error. A regression of an issue
/// that already has a ticket would find that ticket anyway, and one without a ticket was not worth one when
/// it was new either. Warnings and info never open one.
/// </summary>
public static class AutoTicketPolicy
{
    /// <summary>The most tickets one project opens on its own in a UTC day. Anyone holding a DSN can mint new
    /// issues, so without a ceiling a stranger could fill a customer's Linear workspace.</summary>
    public const int PerProjectPerDay = 50;

    /// <summary>How long an automatic ticket waits before it is sent. The consumer reserves it on the
    /// issue's first event before that event's batch reaches the event store, and the ticket's environment
    /// and stack excerpt are read from there.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(20);

    /// <summary>Whether this event could open a ticket in a project that asked for that: a new issue, at
    /// least an error. Asked first, because it needs no query and almost no event passes it.</summary>
    public static bool IsCandidate(bool isNewIssue, Level level) => isNewIssue && level >= Level.Error;

    public static bool ShouldCreate(bool autoCreate, bool isNewIssue, Level level) =>
        autoCreate && IsCandidate(isNewIssue, level);
}
