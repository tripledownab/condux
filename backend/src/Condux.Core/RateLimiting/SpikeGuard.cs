namespace Condux.Core.RateLimiting;

/// <summary>
/// Instance-wide hard cap on ingest throughput — the last line of load shedding
/// before the relay parses a payload. Independent of per-project limits: even if
/// every project is within its own budget, a single relay sheds traffic above
/// this ceiling to protect itself and the downstream pipeline from a spike.
///
/// In-process (per relay instance) and lock-guarded. A rate &lt;= 0 disables the
/// guard entirely (always allows).
/// </summary>
public sealed class SpikeGuard
{
    private readonly double _rate;
    private readonly double _burst;
    private readonly Func<double> _clock;
    private readonly Lock _gate = new();
    private TokenBucket.State _state;

    /// <param name="clock">Monotonic seconds source; defaults to process uptime (injected in tests).</param>
    public SpikeGuard(double ratePerSecond, long burst, Func<double>? clock = null)
    {
        _rate = ratePerSecond;
        _burst = burst;
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
        _state = new TokenBucket.State(burst, _clock());
    }

    /// <summary>True when the guard is off (rate &lt;= 0).</summary>
    public bool Disabled => _rate <= 0;

    /// <summary>Consume one token from the instance-wide bucket.</summary>
    public RateLimitDecision Check()
    {
        if (_rate <= 0)
        {
            return new RateLimitDecision(true, long.MaxValue, 0);
        }

        var now = _clock();
        lock (_gate)
        {
            var (next, decision) = TokenBucket.Step(_state, _rate, _burst, now);
            _state = next;
            return decision;
        }
    }
}
