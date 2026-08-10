namespace Condux.Core.Sampling;

/// <summary>
/// Decides whether a raw event should be persisted to the event store, given how many
/// times its grouped issue has now been seen. Keeps the first <see cref="KeepFirst"/>
/// occurrences of every issue — so new and rare errors are never dropped — then keeps
/// 1-in-<see cref="SampleEvery"/> of the repeats. The grouped-issue counter still counts
/// every occurrence; only redundant raw-event storage is shed. Deterministic (count-based,
/// no randomness), so behavior is reproducible and unit-testable.
/// </summary>
public sealed record IssueSampler(long KeepFirst, long SampleEvery)
{
    /// <summary>Keep-everything sampler (no shedding).</summary>
    public static readonly IssueSampler Unlimited = new(KeepFirst: 0, SampleEvery: 1);

    /// <summary>
    /// Whether to store the raw event for the <paramref name="occurrence"/>-th sighting of
    /// its issue (1-based, as returned by the atomic issue upsert).
    /// </summary>
    public bool ShouldStore(long occurrence)
    {
        if (occurrence <= KeepFirst)
        {
            return true; // always keep the first N of every distinct issue
        }
        if (SampleEvery <= 1)
        {
            return true; // sampling disabled — keep everything
        }
        return occurrence % SampleEvery == 0;
    }
}
