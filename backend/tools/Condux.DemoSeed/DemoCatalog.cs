using Condux.Core.Events;

namespace Condux.DemoSeed;

/// <summary>Where a seeded issue is in its lifecycle. Resolved stamps resolved_at this week; Regressed is an
/// old issue reopened this week (first_seen before the week, activated_at inside it).</summary>
internal enum SeedStatus
{
    Active,
    Resolved,
    Regressed,
}

/// <summary>One realistic demo issue. <c>Frames</c> are "function@path:line", oldest-first (crashing frame
/// last). Volumes drive the issue_stats_1h rollup: <c>WeekEvents</c> in the last 7 days, <c>PrevWeekEvents</c>
/// in the prior 7, so the weekly summary and the charts have week-over-week movement.</summary>
internal sealed record IssueSpec(
    string Fingerprint,
    string Title,
    string Culprit,
    Level Level,
    string Platform,
    string Release,
    int FirstSeenDaysAgo,
    int WeekEvents,
    int PrevWeekEvents,
    SeedStatus Status,
    string ExceptionType,
    string ExceptionMessage,
    string[] Frames);

/// <summary>A seeded Conductor run on an issue. A merged/verified run needs <c>MergedDaysAgo</c> set (the
/// verification flow marks it merged first); a verified run also auto-resolves via the watcher in prod, so
/// its issue is seeded Resolved.</summary>
internal sealed record FixSpec(
    string IssueFingerprint,
    string Model,
    long InputTokens,
    long OutputTokens,
    string Summary,
    int CreatedDaysAgo,
    int? MergedDaysAgo,
    int? VerifiedDaysAgo);

/// <summary>The static demo dataset: a checkout-service project's issues + a few Conductor fixes. Kept as
/// plain data so the seeder is a thin loop and the numbers are easy to tune.</summary>
internal static class DemoCatalog
{
    public const string RepoFullName = "acme/checkout-api";

    public static readonly IReadOnlyList<IssueSpec> Issues =
    [
        new("checkout-undefined-email", "TypeError: Cannot read properties of undefined (reading 'email')",
            "src/checkout/pay.ts in submitOrder", Level.Error, "javascript", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 12, WeekEvents: 142, PrevWeekEvents: 98, SeedStatus.Active,
            "TypeError", "Cannot read properties of undefined (reading 'email')",
            ["processQueue@src/lib/queue.ts:15", "handleClick@src/checkout/Form.tsx:88", "submitOrder@src/checkout/pay.ts:42"]),

        new("cart-items-map", "TypeError: cart.items.map is not a function",
            "src/cart/summary.ts in renderCart", Level.Error, "javascript", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 9, WeekEvents: 88, PrevWeekEvents: 40, SeedStatus.Active,
            "TypeError", "cart.items.map is not a function",
            ["render@src/cart/CartPage.tsx:31", "renderCart@src/cart/summary.ts:64"]),

        new("payment-gateway-timeout", "Error: Payment gateway timeout after 30000ms",
            "src/payments/gateway.ts in charge", Level.Error, "node", "checkout-api@1.4.1",
            FirstSeenDaysAgo: 14, WeekEvents: 61, PrevWeekEvents: 55, SeedStatus.Active,
            "Error", "Payment gateway timeout after 30000ms",
            ["charge@src/payments/gateway.ts:120", "authorize@src/payments/service.ts:47", "post@node_modules/undici/index.js:210"]),

        new("redis-econnrefused", "Error: connect ECONNREFUSED redis:6379",
            "src/lib/cache.ts in getSession", Level.Error, "node", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 6, WeekEvents: 44, PrevWeekEvents: 0, SeedStatus.Active,
            "Error", "connect ECONNREFUSED redis:6379",
            ["getSession@src/lib/cache.ts:22", "onConnect@node_modules/ioredis/built/Redis.js:333"]),

        new("pg-too-many-clients", "FATAL: sorry, too many clients already",
            "src/db/pool.ts in acquire", Level.Fatal, "node", "checkout-api@1.4.1",
            FirstSeenDaysAgo: 11, WeekEvents: 18, PrevWeekEvents: 9, SeedStatus.Active,
            "DatabaseError", "sorry, too many clients already",
            ["acquire@src/db/pool.ts:58", "query@src/db/client.ts:19"]),

        new("invalid-coupon", "ValidationError: invalid coupon code",
            "src/checkout/coupons.ts in applyCoupon", Level.Warning, "node", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 8, WeekEvents: 33, PrevWeekEvents: 21, SeedStatus.Active,
            "ValidationError", "invalid coupon code",
            ["applyCoupon@src/checkout/coupons.ts:41", "validate@src/checkout/validate.ts:12"]),

        new("max-call-stack", "RangeError: Maximum call stack size exceeded",
            "src/pricing/discount.ts in resolve", Level.Error, "javascript", "checkout-api@1.3.9",
            FirstSeenDaysAgo: 20, WeekEvents: 12, PrevWeekEvents: 40, SeedStatus.Resolved,
            "RangeError", "Maximum call stack size exceeded",
            ["resolve@src/pricing/discount.ts:77", "resolve@src/pricing/discount.ts:79"]),

        new("cart-total-null", "TypeError: Cannot read properties of null (reading 'total')",
            "src/cart/totals.ts in computeTotal", Level.Error, "javascript", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 18, WeekEvents: 54, PrevWeekEvents: 12, SeedStatus.Regressed,
            "TypeError", "Cannot read properties of null (reading 'total')",
            ["checkout@src/checkout/CheckoutPage.tsx:26", "computeTotal@src/cart/totals.ts:33"]),

        new("stripe-402", "Error: Stripe API 402 Payment Required",
            "src/payments/stripe.ts in createCharge", Level.Error, "node", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 5, WeekEvents: 27, PrevWeekEvents: 0, SeedStatus.Active,
            "StripeError", "402 Payment Required",
            ["createCharge@src/payments/stripe.ts:64", "request@node_modules/stripe/lib/StripeResource.js:180"]),

        new("fetch-aborted", "AbortError: The operation was aborted",
            "src/lib/http.ts in fetchWithTimeout", Level.Warning, "javascript", "checkout-api@1.4.2",
            FirstSeenDaysAgo: 4, WeekEvents: 15, PrevWeekEvents: 0, SeedStatus.Active,
            "AbortError", "The operation was aborted",
            ["fetchWithTimeout@src/lib/http.ts:29", "loadInventory@src/inventory/api.ts:18"]),
    ];

    public static readonly IReadOnlyList<FixSpec> Fixes =
    [
        new("checkout-undefined-email", "claude-opus-4-8", 42000, 8000,
            "## Fix\nGuard `submitOrder` against a missing `user` before reading `user.email`, falling back to the "
                + "session email. Adds a regression test for the anonymous-checkout path.",
            CreatedDaysAgo: 2, MergedDaysAgo: null, VerifiedDaysAgo: null),

        new("payment-gateway-timeout", "claude-opus-4-8", 38000, 6500,
            "## Fix\nRaise the gateway client timeout to 45s and add one bounded retry with jitter on a timeout, "
                + "so a slow-but-healthy gateway no longer surfaces as an error.",
            CreatedDaysAgo: 9, MergedDaysAgo: 2, VerifiedDaysAgo: null),

        new("max-call-stack", "claude-opus-4-8", 51000, 9000,
            "## Fix\nBreak the mutual recursion in `resolve` by memoizing already-resolved discount tiers; the "
                + "stack no longer grows unbounded for stacked promotions.",
            CreatedDaysAgo: 10, MergedDaysAgo: 8, VerifiedDaysAgo: 1),

        new("invalid-coupon", "claude-opus-4-8", 22000, 4000,
            "## Fix\nReturn a 422 with a clear message for an unknown coupon instead of throwing, and log the code "
                + "at info level so invalid coupons stop paging as errors.",
            CreatedDaysAgo: 1, MergedDaysAgo: null, VerifiedDaysAgo: null),
    ];
}
