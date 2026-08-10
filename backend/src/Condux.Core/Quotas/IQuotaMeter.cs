namespace Condux.Core.Quotas;

/// <summary>The outcome of a monthly-quota check: whether the event is admitted, plus the current usage
/// and remaining allowance for response headers. <c>Remaining</c> is <c>long.MaxValue</c> for an
/// unlimited (Enterprise) tier.</summary>
public readonly record struct QuotaDecision(bool Allowed, long Used, long Remaining);

/// <summary>
/// A per-key monthly event quota. The <paramref name="monthlyLimit"/> is supplied per call so the relay
/// sources it from the project's plan tier (<c>PlanCatalog.Limits.MonthlyEvents</c>) at request time; a
/// limit &lt;= 0 means unlimited. <see cref="TryConsumeAsync"/> counts one event only when it is admitted,
/// so a rejected event never consumes quota. Two implementations sit behind this: an in-process meter
/// (<see cref="InMemoryQuotaMeter"/>, for dev/tests and single-relay setups) and a Valkey-backed one
/// (one budget shared across relay replicas).
/// </summary>
public interface IQuotaMeter
{
    /// <summary>Admit and count one event for <paramref name="key"/> (a project id) in the current month.</summary>
    ValueTask<QuotaDecision> TryConsumeAsync(string key, long monthlyLimit, CancellationToken ct = default);
}
