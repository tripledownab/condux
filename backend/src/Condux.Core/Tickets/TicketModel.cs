namespace Condux.Core.Tickets;

// The ticket domain's values (ADR-0036, amended by ADR-0053): which tracker, where a ticket is in its life
// and why one could not be created. Numeric values are stored, so they never change.

/// <summary>A tracker Condux can open tickets in. Values match the <c>provider</c> columns.</summary>
public enum TicketProvider
{
    Linear = 1,
}

/// <summary>Where an <c>issue_tickets</c> row is. Values match <c>issue_tickets.state</c>.</summary>
public enum TicketState
{
    /// <summary>Reserved and waiting for the worker. Nothing has been sent.</summary>
    Pending = 1,

    /// <summary>The worker has taken it and may be calling the tracker right now.</summary>
    Claimed = 2,

    Created = 3,

    /// <summary>The tracker said no, or there was nowhere to send it. Safe to retry.</summary>
    Failed = 4,

    /// <summary>A call may have reached the tracker without our hearing the answer, so the ticket may
    /// exist. Never claimed again on its own; a person decides, after checking.</summary>
    Uncertain = 5,
}

/// <summary>Why a ticket was not created: a closed set, so a tracker's own error text never reaches the
/// dashboard. Values match <c>issue_tickets.failure</c>.</summary>
public enum TicketFailure
{
    /// <summary>The project has no team chosen.</summary>
    NotMapped = 1,

    /// <summary>The org has no connection to the tracker.</summary>
    NotConnected = 2,

    /// <summary>The tracker refused to renew our grant, so the org has to reconnect.</summary>
    ConnectionBroken = 3,

    /// <summary>The tracker refused the team, which usually means its admins withdrew the app from it.</summary>
    TeamUnavailable = 4,

    /// <summary>The tracker refused the ticket for a reason that is none of the above.</summary>
    Refused = 5,
}

/// <summary>What goes into a ticket. Built by <see cref="TicketText"/>, which owns the escaping.</summary>
public sealed record TicketContent(string Title, string Body);

/// <summary>A created ticket: its identifier as the tracker shows it (<c>ENG-123</c>) and its URL.</summary>
public sealed record TicketRef(string Key, string Url);

/// <summary>What one attempt established.</summary>
public enum TicketOutcomeKind
{
    Created = 1,

    /// <summary>Definitely not created, for <see cref="TicketOutcome.Failure"/>.</summary>
    Failed = 2,

    /// <summary>The call may have reached the tracker and we did not hear the answer.</summary>
    Uncertain = 3,

    /// <summary>Definitely not created, and worth trying again unprompted: the tracker asked us to slow
    /// down, or we could not reach it at all.</summary>
    RetryLater = 4,
}

/// <summary>The answer from one attempt: a kind, with the ticket when created and the reason when failed.</summary>
public sealed record TicketOutcome(TicketOutcomeKind Kind, TicketRef? Created = null, TicketFailure? Failure = null)
{
    public static TicketOutcome Success(TicketRef created) => new(TicketOutcomeKind.Created, Created: created);

    public static TicketOutcome Fail(TicketFailure failure) => new(TicketOutcomeKind.Failed, Failure: failure);

    public static readonly TicketOutcome Uncertain = new(TicketOutcomeKind.Uncertain);

    public static readonly TicketOutcome RetryLater = new(TicketOutcomeKind.RetryLater);
}

/// <summary>
/// Opens a ticket in one tracker. The implementation owns its credentials, including renewing them, so a
/// caller names only the org, the target, the labels and the content. <paramref name="idempotencyId"/> is
/// sent with every attempt for one reserved row, so a retry asks the tracker for the same ticket rather than
/// a new one.
/// </summary>
public interface ITicketSink
{
    Task<TicketOutcome> CreateAsync(
        long orgId, string targetId, IReadOnlyList<string> labelIds, Guid idempotencyId, TicketContent content,
        CancellationToken cancellationToken = default);
}
