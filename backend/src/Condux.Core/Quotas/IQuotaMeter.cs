namespace Condux.Core.Quotas;

/// <summary>The outcome of a monthly-quota check: how many of the requested events were admitted, plus the
/// current usage and remaining allowance for response headers. <c>Admitted</c> may be less than the number
/// asked for when the batch straddles the cap, so a caller that sent several events publishes the first
/// <c>Admitted</c> of them and reports the rest as rejected. <c>Remaining</c> is <c>long.MaxValue</c> for an
/// unlimited (Enterprise) tier.</summary>
public readonly record struct QuotaDecision(long Admitted, long Used, long Remaining)
{
    /// <summary>Whether anything at all was admitted. For a caller asking about one event this is the
    /// whole answer, which is why it stays on the type rather than being spelled out at each such site.
    /// </summary>
    public bool Allowed => Admitted > 0;
}

/// <summary>
/// A per-key monthly event quota. The <paramref name="monthlyLimit"/> is supplied per call so the relay
/// sources it from the project's plan tier (<c>PlanCatalog.Limits.MonthlyEvents</c>) at request time; a
/// limit &lt;= 0 means unlimited. <see cref="TryConsumeAsync"/> counts only the events it admits, so a
/// rejected event never consumes quota. Two implementations sit behind this: an in-process meter
/// (<see cref="InMemoryQuotaMeter"/>, for dev/tests and single-relay setups) and a Valkey-backed one
/// (one budget shared across relay replicas).
/// </summary>
public interface IQuotaMeter
{
    /// <summary>
    /// Admit and count up to <paramref name="count"/> events for <paramref name="key"/> (a project id) in
    /// the current month. The count is a parameter rather than a loop at the call site because one OTLP
    /// export carries a record count the sender chose: metering per record turns one request into that
    /// many round trips to a shared counter, which is work a stranger sizes.
    /// </summary>
    ValueTask<QuotaDecision> TryConsumeAsync(
        string key, long monthlyLimit, long count = 1, CancellationToken ct = default);

    /// <summary>
    /// Give <paramref name="count"/> events of <paramref name="key"/>'s current month back, for events
    /// that were admitted and then could not be stored.
    /// </summary>
    /// <remarks>
    /// Consuming quota cannot be undone by the request that did it, so a caller that takes a batch and
    /// then fails part way through has spent the remainder on nothing. That was worth ignoring while a
    /// caller only ever took one event: the cost of a failed publish was one event of a month. Taking a
    /// batch of up to 20,000 made it worth the round trip. Reserve-then-give-back is the shape the
    /// Conductor's own allowance already reaches for (<c>IAiFixQuota.RefundAsync</c>), borrowed here as a
    /// design rather than shared as behaviour: that one counts fix runs in Postgres, this counts events.
    ///
    /// It never lowers the counter below zero, so a duplicate or late refund cannot manufacture quota.
    /// Two ways it can still under-count, both left alone deliberately. It refunds into the wrong month
    /// if the clock rolls over between the two calls. And a Valkey meter that failed open on the way in
    /// counted nothing, so if the store recovers before the refund the give-back comes off usage that was
    /// real. Both cost at most one batch, and the fail-open they sit behind already grants a project
    /// unmetered ingest for the length of the outage, which is the larger and deliberate leak.
    /// </remarks>
    ValueTask RefundAsync(string key, long count, CancellationToken ct = default);

    /// <summary>
    /// The precondition on a <paramref name="count"/>, stated once so the two meters must agree on it:
    /// pinned by InMemoryQuotaMeterTests and pinned by ValkeyQuotaMeterTest, which assert the same
    /// refusal on each. Asking to consume nothing has no honest decision to return, asking to refund
    /// nothing is a call that should not have been made, and either one taking a negative would run the
    /// opposite operation, since both meters move the counter by whatever they are given. All are caller
    /// bugs, so they throw rather than resolve to some default.
    /// </summary>
    public static void RequirePositiveCount(long count) =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
}
