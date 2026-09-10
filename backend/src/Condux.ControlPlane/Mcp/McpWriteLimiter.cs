using Condux.Core.RateLimiting;

namespace Condux.ControlPlane.Mcp;

/// <summary>
/// How often one MCP token may write (ADR-0046). Reads are not limited, so nothing that worked before
/// this existed behaves differently.
///
/// The thing being guarded against is an agent loop, not an attacker: a model that misreads a tool result
/// can call the same tool until something stops it, and the writes are cheap enough that nothing else
/// would. A burst covers triaging a backlog in one go; the sustained rate is what a loop runs into.
///
/// In process, so the budget is per replica rather than shared. That is adequate here because both writes
/// are reversible and cost nothing to undo, and it would not be for a tool that spends money. A fix tool
/// needs a shared counter, not this.
/// </summary>
internal sealed class McpWriteLimiter(Func<double>? clock = null)
{
    private const double WritesPerSecond = 1.0;

    /// <summary>Writes allowed back to back before the sustained rate binds. Exposed so a test can state
    /// the boundary it is checking instead of hardcoding a number that would drift from this one.</summary>
    public const long Burst = 20;

    // The clock is injectable for the same reason InMemoryRateLimiter's is: refill is a function of
    // elapsed time, and a test that waited for real seconds would be slow and flaky at once.
    private readonly InMemoryRateLimiter limiter = new(clock);

    /// <summary>Consumes one write from the token's budget; false when it is exhausted.</summary>
    public async Task<bool> TryWriteAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var decision = await limiter.CheckAsync(
            $"mcp-write:{tokenId}", WritesPerSecond, Burst, cancellationToken);
        return decision.Allowed;
    }
}
