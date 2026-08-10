using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Billing;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Billing;
using Condux.Core.Plans;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Stripe billing (#billing). Opt-in behind <see cref="StripeConfig"/>; when unset the routes 404.
/// <c>GET .../billing</c> (member+) reports the org's plan + purchasable tiers; <c>POST .../billing/checkout</c>
/// (admin+) opens a hosted Stripe Checkout for a tier and returns its URL; <c>POST /api/stripe/webhook</c>
/// (no cookie auth — authenticated by the Stripe signature) applies subscription changes to
/// <c>orgs.tier</c>. Tiers live only in <see cref="PlanCatalog"/>; the price-&gt;tier map is env config.
/// </summary>
internal static class BillingEndpoints
{
    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orgs/{orgId:long}/billing",
                async Task<Results<Ok<BillingStatusResponse>, NotFound>> (
                    long orgId, StripeConfig config, OrgRepository orgs, HttpContext http) =>
                {
                    if (await orgs.GetAsync(orgId, http.RequestAborted) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }

                    var purchasable = config.Enabled
                        ? config.Prices.Keys.OrderBy(t => (int)t).Select(t => t.ToString()).ToList()
                        : [];
                    return TypedResults.Ok(new BillingStatusResponse(
                        ((Tier)org.Tier).ToString(), config.Enabled, purchasable,
                        HasSubscription: !string.IsNullOrEmpty(org.StripeSubscriptionId)));
                })
            .WithName("billingStatus").WithTags("Billing")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapPost("/api/orgs/{orgId:long}/billing/checkout",
                async Task<Results<Ok<CheckoutResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long orgId, CheckoutRequest req, StripeConfig config, StripeClient stripe,
                    OrgRepository orgs, IConfiguration cfg, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    if (!Enum.TryParse<Tier>(req.Tier, ignoreCase: true, out var tier)
                        || config.PriceForTier(tier) is not { } priceId)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("tier_not_purchasable"));
                    }

                    if (await orgs.GetAsync(orgId, http.RequestAborted) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }

                    var origin = AppUrls.BaseUrl(cfg);
                    var url = await stripe.CreateCheckoutSessionAsync(
                        orgId, priceId, tier.ToString(), org.StripeCustomerId,
                        successUrl: $"{origin}/settings/general?billing=success",
                        cancelUrl: $"{origin}/settings/general?billing=cancelled",
                        http.RequestAborted);

                    return url is null
                        ? TypedResults.BadRequest(new ErrorResponse("checkout_failed"))
                        : TypedResults.Ok(new CheckoutResponse(url));
                })
            .WithName("billingCheckout").WithTags("Billing")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // Self-serve subscription management: open Stripe's hosted billing portal for the org's own customer
        // so an admin can update the card, switch/downgrade the plan, cancel, and see invoices — all in
        // Stripe's UI. The resulting subscription webhooks reconcile orgs.tier, so we never write it here.
        app.MapPost("/api/orgs/{orgId:long}/billing/portal",
                async Task<Results<Ok<CheckoutResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long orgId, StripeConfig config, StripeClient stripe, OrgRepository orgs,
                    IConfiguration cfg, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await orgs.GetAsync(orgId, http.RequestAborted) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }
                    if (string.IsNullOrEmpty(org.StripeCustomerId))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("no_subscription"));
                    }

                    var url = await stripe.CreateBillingPortalSessionAsync(
                        org.StripeCustomerId, $"{AppUrls.BaseUrl(cfg)}/settings/general", http.RequestAborted);
                    return url is null
                        ? TypedResults.BadRequest(new ErrorResponse("portal_failed"))
                        : TypedResults.Ok(new CheckoutResponse(url));
                })
            .WithName("billingPortal").WithTags("Billing")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // Stripe -> us (server to server). No cookie auth; the Stripe-Signature header is the authenticator.
        app.MapPost("/api/stripe/webhook",
                async Task<Results<Ok, UnauthorizedHttpResult, NotFound>> (
                    StripeConfig config, OrgRepository orgs, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    using var reader = new StreamReader(http.Request.Body);
                    var payload = await reader.ReadToEndAsync(http.RequestAborted);
                    if (!StripeSignature.Verify(
                            payload, http.Request.Headers["Stripe-Signature"], config.WebhookSecret!,
                            DateTimeOffset.UtcNow))
                    {
                        return TypedResults.Unauthorized();
                    }

                    if (StripeEvents.Parse(payload) is { } evt)
                    {
                        await ApplyAsync(evt, config, orgs, http.RequestAborted);
                    }

                    return TypedResults.Ok(); // ack so Stripe stops retrying
                })
            .WithName("stripeWebhook").WithTags("Billing");
    }

    // Map a verified event to an orgs.tier change. Idempotent — Stripe can redeliver a webhook.
    private static async Task ApplyAsync(
        StripeEvent evt, StripeConfig config, OrgRepository orgs, CancellationToken ct)
    {
        switch (evt.Type)
        {
            // Checkout done: link the new Stripe customer to the org (carried as client_reference_id), and
            // set the purchased tier from the session metadata. Applying the tier HERE — not only from the
            // subscription event — makes it order-independent: Stripe may deliver customer.subscription.created
            // first, where the tier apply keys on the customer link this handler makes, so on its own it would
            // miss. ApplyTierByStripeCustomerAsync runs after the link above, so it now resolves the org.
            case "checkout.session.completed"
                when long.TryParse(
                        evt.ClientReferenceId, NumberStyles.None, CultureInfo.InvariantCulture, out var orgId)
                    && evt.CustomerId is { } customer:
                await orgs.LinkStripeCustomerAsync(orgId, customer, evt.SubscriptionId, ct);
                if (evt.TierName is { } name && Enum.TryParse<Tier>(name, ignoreCase: true, out var boughtTier)
                    && config.PriceForTier(boughtTier) is not null)
                {
                    await orgs.ApplyTierByStripeCustomerAsync(customer, (int)boughtTier, evt.SubscriptionId, ct);
                }
                break;

            // Subscription active: set the org to the tier the price maps to.
            case "customer.subscription.created" or "customer.subscription.updated"
                when evt.CustomerId is { } customer && config.TierForPrice(evt.PriceId) is { } tier
                    && evt.Status is "active" or "trialing":
                await orgs.ApplyTierByStripeCustomerAsync(customer, (int)tier, evt.SubscriptionId, ct);
                break;

            // Subscription gone: downgrade to Free.
            case "customer.subscription.deleted" when evt.CustomerId is { } customer:
                await orgs.DowngradeByStripeCustomerAsync(customer, ct);
                break;
        }
    }
}
