using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Billing;
using Condux.ControlPlane.Setup;
using Condux.Core.Plans;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Full admin billing control (ADR-0027). An operator can cancel a subscription (at period end), change
/// its plan, and open the Stripe billing portal for anything deeper (invoices, refunds, hard cancel).
/// Every action drives the Stripe API only; the existing <see cref="BillingEndpoints"/> webhook remains
/// the single writer of <c>orgs.tier</c>, so this never touches the tier directly (ADR-0026 preserved).
/// All routes 404 when Stripe is not configured, mirroring the tenant billing surface.
/// </summary>
internal static class AdminBillingEndpoints
{
    /// <summary>The org's subscription view (also embedded in the org detail response). Shared so the
    /// admin detail page and the billing tab agree on one shape.</summary>
    public static AdminBillingResponse BuildStatus(Org org, StripeConfig config) => new(
        ((Tier)org.Tier).ToString(),
        config.Enabled,
        HasSubscription: !string.IsNullOrEmpty(org.StripeSubscriptionId),
        org.StripeCustomerId,
        org.StripeSubscriptionId,
        config.Enabled
            ? config.Prices.Keys.OrderBy(t => (int)t).Select(t => t.ToString()).ToList()
            : []);

    public static void MapAdminBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/orgs/{orgId:long}/billing",
                async Task<Results<Ok<AdminBillingResponse>, NotFound>> (
                    long orgId, HttpContext http, StripeConfig config, OrgRepository orgs) =>
                    await orgs.GetAsync(orgId, http.RequestAborted) is { } org
                        ? TypedResults.Ok(BuildStatus(org, config))
                        : TypedResults.NotFound())
            .WithName("adminBillingStatus").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapPost("/api/admin/orgs/{orgId:long}/billing/cancel",
                async Task<Results<Accepted, NotFound, BadRequest<ErrorResponse>>> (
                    long orgId, HttpContext http, StripeConfig config, StripeClient stripe,
                    OrgRepository orgs, AdminAuditRepository audit) =>
                {
                    var ct = http.RequestAborted;
                    if (!config.Enabled || await orgs.GetAsync(orgId, ct) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }
                    if (string.IsNullOrEmpty(org.StripeSubscriptionId))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("no_subscription"));
                    }
                    if (!await stripe.CancelSubscriptionAsync(org.StripeSubscriptionId, ct))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("stripe_error"));
                    }

                    await AdminAudit.WriteAsync(http, audit, "billing.cancel", orgId,
                        details: new { subscriptionId = org.StripeSubscriptionId });
                    return TypedResults.Accepted((string?)null);
                })
            .WithName("adminBillingCancel").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapPost("/api/admin/orgs/{orgId:long}/billing/plan",
                async Task<Results<Accepted, NotFound, BadRequest<ErrorResponse>>> (
                    long orgId, AdminChangePlanRequest req, HttpContext http, StripeConfig config,
                    StripeClient stripe, OrgRepository orgs, AdminAuditRepository audit) =>
                {
                    var ct = http.RequestAborted;
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (!Enum.TryParse<Tier>(req.Tier, ignoreCase: true, out var tier)
                        || config.PriceForTier(tier) is not { } priceId)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("tier_not_purchasable"));
                    }
                    if (await orgs.GetAsync(orgId, ct) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }
                    if (string.IsNullOrEmpty(org.StripeSubscriptionId))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("no_subscription"));
                    }
                    if (!await stripe.ChangePlanAsync(org.StripeSubscriptionId, priceId, ct))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("stripe_error"));
                    }

                    await AdminAudit.WriteAsync(http, audit, "billing.plan_change", orgId, details: new
                    {
                        toTier = tier.ToString(),
                        priceId,
                        subscriptionId = org.StripeSubscriptionId,
                    });
                    return TypedResults.Accepted((string?)null);
                })
            .WithName("adminBillingChangePlan").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapPost("/api/admin/orgs/{orgId:long}/billing/portal",
                async Task<Results<Ok<AdminPortalResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long orgId, HttpContext http, StripeConfig config, StripeClient stripe,
                    OrgRepository orgs, IConfiguration cfg, AdminAuditRepository audit) =>
                {
                    var ct = http.RequestAborted;
                    if (!config.Enabled || await orgs.GetAsync(orgId, ct) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }
                    if (string.IsNullOrEmpty(org.StripeCustomerId))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("no_customer"));
                    }

                    var returnUrl = $"{AppUrls.BaseUrl(cfg)}/admin/organizations/{orgId}";
                    var url = await stripe.CreateBillingPortalSessionAsync(org.StripeCustomerId, returnUrl, ct);
                    if (url is null)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("stripe_error"));
                    }

                    await AdminAudit.WriteAsync(http, audit, "billing.portal", orgId,
                        details: new { customerId = org.StripeCustomerId });
                    return TypedResults.Ok(new AdminPortalResponse(url));
                })
            .WithName("adminBillingPortal").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());
    }
}
