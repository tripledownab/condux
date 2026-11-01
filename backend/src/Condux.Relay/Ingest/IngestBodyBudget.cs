using Condux.Core.Http;

namespace Condux.Relay.Ingest;

/// <summary>
/// How much of the ingest bodies one relay holds in memory at once, from every project, and how that is
/// divided: each organisation reads within a ceiling of its own, and each address sending for it within a
/// share of that ceiling (<see cref="IngestSenders"/>). A DSN is public by design and each free signup brings
/// more projects, so the per-project rate limit does not bound how many bodies arrive together. The body is
/// read after decompression, so it is charged on what it expands to: a gzip body of a few kilobytes that
/// expands to the ingest cap costs what it expanded to, not what was sent.
/// </summary>
internal sealed class IngestBodyBudget(long bytes, long orgBytes, long senderBytes) : ByteBudget(bytes)
{
    private const long DefaultBytes = 256L * 1024 * 1024;
    private const int OrgCeilings = 4;
    private const int SendersPerOrg = 2;

    // Ceilings and shares with a body being read now. Each is dropped once its last reader is done and
    // nothing is charged to it, so the counts follow what is in flight, not everyone who ever sent.
    private readonly Dictionary<string, Share> _orgs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Share> _senders = new(StringComparer.Ordinal);

    /// <summary>The budget for a relay whose bodies may reach <paramref name="maxIngestBytes"/>. A read is
    /// charged about twice its body while it arrives (RequestBodies.ReadHeldAsync), so a sender's share holds
    /// one body at the cap with room to spare, or raising the cap would leave a body no share could ever
    /// admit, answered 429 on every retry. An organisation's ceiling holds two such shares, and the budget
    /// several ceilings, but never more than half of <paramref name="availableBytes"/>, the memory the
    /// runtime may use: a budget past that is met by the process running out of memory before any body is
    /// refused, and the rest is the runtime's and Kestrel's own.</summary>
    public static IngestBodyBudget ForCap(long maxIngestBytes, long availableBytes)
    {
        var sender = 2 * maxIngestBytes + 1024 * 1024;
        var org = SendersPerOrg * sender;
        var bytes = Math.Min(Math.Max(DefaultBytes, OrgCeilings * org), availableBytes / 2);
        return new IngestBodyBudget(bytes, org, sender);
    }

    /// <summary>How many ceilings and shares are held now.</summary>
    public int SharesHeld
    {
        get
        {
            lock (_orgs)
            {
                return _orgs.Count + _senders.Count;
            }
        }
    }

    /// <summary>The share <paramref name="address"/> reads into for <paramref name="orgKey"/>, within that
    /// organisation's ceiling, held until the lease is disposed. Every charge to it is a charge to the
    /// ceiling and to this budget too.</summary>
    public ShareLease LeaseFor(string orgKey, string address)
    {
        var senderKey = $"{orgKey}@{address}";
        lock (_orgs)
        {
            var org = Enter(_orgs, orgKey, () => new Share(orgBytes, this));
            var sender = Enter(_senders, senderKey, () => new Share(senderBytes, org));
            return new ShareLease(sender, () =>
            {
                lock (_orgs)
                {
                    Leave(_senders, senderKey, sender);
                    Leave(_orgs, orgKey, org);
                }
            });
        }
    }

    private static Share Enter(Dictionary<string, Share> shares, string key, Func<Share> create)
    {
        if (!shares.TryGetValue(key, out var share))
        {
            share = create();
            shares[key] = share;
        }
        share.Readers++;
        return share;
    }

    // Dropped only once nothing is charged to it: one created again starts full, so dropping one still
    // holding bytes would let its holder hold them twice over.
    private static void Leave(Dictionary<string, Share> shares, string key, Share share)
    {
        if (--share.Readers == 0 && share.IsFull)
        {
            shares.Remove(key);
        }
    }

    private sealed class Share(long bytes, ByteBudget within) : ByteBudget(bytes, within)
    {
        private readonly long _size = bytes;

        public int Readers; // guarded by the owner's lock

        public bool IsFull => Free == _size;
    }

    /// <summary>A sender's share, held while one of its bodies is read and worked on.</summary>
    internal sealed class ShareLease(ByteBudget budget, Action returnShare) : IDisposable
    {
        private int _returned;

        public ByteBudget Budget => budget;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                returnShare();
            }
        }
    }
}
