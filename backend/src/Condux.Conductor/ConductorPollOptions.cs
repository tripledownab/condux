using Condux.Core.FixEngine;

namespace Condux.Conductor;

/// <summary>
/// The fix provider's poll cadence, env-tunable at last (the ManagedAgentOptions comment always said
/// the Conductor could override it; now it can). The managed-agents provider gets a longer default
/// ceiling — a vendor-hosted session legitimately runs tens of minutes, where the in-process gateway's
/// ten were plenty.
/// </summary>
internal static class ConductorPollOptions
{
    /// <summary>The longest a run may be polled. The stale-run sweep waits twice this, plus a margin, before
    /// failing a run, so a larger budget only delays the refund of a run the Conductor lost, and an unbounded one
    /// overflows the arithmetic the sweep does with it.</summary>
    internal static readonly TimeSpan MaxPollBudget = TimeSpan.FromDays(1);

    public static ManagedAgentOptions FromEnv(IConfiguration config, string providerKind)
    {
        var defaults = providerKind == "managed-agents"
            ? new ManagedAgentOptions(TimeSpan.FromSeconds(10), MaxPolls: 240) // 40 min ceiling
            : ManagedAgentOptions.Default;

        var seconds = Read(config, "CONDUX_CONDUCTOR_POLL_SECONDS", (int)defaults.PollInterval.TotalSeconds);
        var maxPolls = Read(config, "CONDUX_CONDUCTOR_MAX_POLLS", defaults.MaxPolls);
        // Multiplied as longs, so the check itself cannot overflow.
        if ((long)seconds * maxPolls > (long)MaxPollBudget.TotalSeconds)
        {
            throw new InvalidOperationException(
                $"CONDUX_CONDUCTOR_POLL_SECONDS x CONDUX_CONDUCTOR_MAX_POLLS must not exceed {MaxPollBudget.TotalSeconds:0} seconds, got {seconds} x {maxPolls}.");
        }
        return new ManagedAgentOptions(TimeSpan.FromSeconds(seconds), maxPolls);
    }

    private static int Read(IConfiguration config, string key, int fallback)
    {
        if (config[key] is not { Length: > 0 } raw)
        {
            return fallback;
        }
        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{key} must be a positive integer, got '{raw}'.");
        }
        return value;
    }
}
