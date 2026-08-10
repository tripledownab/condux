namespace Condux.Core.RateLimiting;

/// <summary>The outcome of a rate-limit check, used to shape the HTTP response.</summary>
public readonly record struct RateLimitDecision(bool Allowed, long Remaining, long RetryAfterSeconds);
