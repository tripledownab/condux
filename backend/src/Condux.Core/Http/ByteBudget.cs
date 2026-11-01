namespace Condux.Core.Http;

/// <summary>
/// How many bytes one kind of work may hold in memory at once, shared by every caller and charged as the
/// work allocates, so what a caller holds is what it has been charged for. A charge past what is free is refused at
/// once rather than waited for: readers that each held part of the budget and waited for more would wait on
/// each other for good. Each use names its own subclass, so a service registers one per kind of work.
///
/// A budget may sit <paramref name="within"/> another, as one tenant's share of a budget every tenant
/// draws on: a charge is then taken from both or from neither, and given back to both.
/// </summary>
public abstract class ByteBudget(long bytes, ByteBudget? within = null)
{
    private long _free = bytes;

    /// <summary>How many bytes are free now.</summary>
    protected long Free => Volatile.Read(ref _free);

    /// <summary>Takes <paramref name="count"/> bytes from the budget, and from the one it sits within, or
    /// nothing when fewer are free in either.</summary>
    public bool TryReserve(long count)
    {
        if (!TryTake(count))
        {
            return false;
        }
        if (within is not null && !within.TryReserve(count))
        {
            Interlocked.Add(ref _free, count);
            return false;
        }
        return true;
    }

    /// <summary>Gives back bytes an earlier <see cref="TryReserve"/> took.</summary>
    public void Release(long count)
    {
        Interlocked.Add(ref _free, count);
        within?.Release(count);
    }

    private bool TryTake(long count)
    {
        var free = Volatile.Read(ref _free);
        while (free >= count)
        {
            var seen = Interlocked.CompareExchange(ref _free, free - count, free);
            if (seen == free)
            {
                return true;
            }
            free = seen;
        }
        return false;
    }
}
