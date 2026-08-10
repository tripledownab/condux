using System.Globalization;
using System.Text.Json;
using Condux.ControlPlane.Setup;
using Condux.Telemetry;
using Microsoft.Extensions.Logging;

namespace Condux.ControlPlane.Billing;

/// <summary>
/// The billing flow's thin Stripe client: create a subscription Checkout Session, open a billing portal,
/// and cancel/change a subscription. A thin <see cref="HttpClient"/> (Bearer secret key, form-encoded body)
/// — no Stripe SDK, matching the hand-rolled Anthropic/GitHub clients. A non-2xx response is logged and
/// self-reported to Condux (<see cref="LogFailureAsync"/>), then surfaced as null/false, never swallowed
/// silently. The checkout session is tagged with the org id as <c>client_reference_id</c>, which the webhook
/// reads back to link the resulting Stripe customer to the org.
/// </summary>
internal sealed class StripeClient(
    HttpClient http, StripeConfig config, ILogger<StripeClient> logger, ConduxSelfReporter selfReporter)
{
    // Overridable so a test can point the call at a stub instead of the live Stripe API.
    public string ApiBase { get; init; } = "https://api.stripe.com/v1";

    public async Task<string?> CreateCheckoutSessionAsync(
        long orgId, string priceId, string tierName, string? customerId,
        string successUrl, string cancelUrl, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("line_items[0][price]", priceId),
            new("line_items[0][quantity]", "1"),
            new("client_reference_id", orgId.ToString(CultureInfo.InvariantCulture)),
            // Stamp the purchased tier so the completed-checkout webhook can set orgs.tier itself, rather
            // than depending on customer.subscription.created arriving after the customer link is made.
            new("metadata[condux_tier]", tierName),
            new("success_url", successUrl),
            new("cancel_url", cancelUrl),
            new("allow_promotion_codes", "true"),
        };
        // Reuse the org's existing Stripe customer when it has one, so subscriptions don't fan out across
        // duplicate customers.
        if (!string.IsNullOrEmpty(customerId))
        {
            form.Add(new("customer", customerId));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/checkout/sessions")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new("Bearer", config.SecretKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync("checkout.sessions.create", response, ct);
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    /// <summary>Cancel a subscription at the end of its current period (ADR-0027 admin billing control).
    /// Graceful: the org keeps the plan it paid for until then; the resulting <c>subscription.updated</c>
    /// (then <c>.deleted</c> at period end) webhook is what reconciles <c>orgs.tier</c>. True on success.</summary>
    public async Task<bool> CancelSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/subscriptions/{subscriptionId}")
        {
            Content = new FormUrlEncodedContent([new("cancel_at_period_end", "true")]),
        };
        request.Headers.Authorization = new("Bearer", config.SecretKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync("subscriptions.cancel", response, ct);
            return false;
        }
        return true;
    }

    /// <summary>Move a subscription to a different plan by swapping its single line item's price, with
    /// proration. The <c>subscription.updated</c> webhook maps the new price back to a tier (the single
    /// writer of <c>orgs.tier</c>). True on success.</summary>
    public async Task<bool> ChangePlanAsync(string subscriptionId, string newPriceId, CancellationToken ct)
    {
        // Stripe needs the existing item id to swap its price (rather than add a second line item).
        using var getRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{ApiBase}/subscriptions/{subscriptionId}");
        getRequest.Headers.Authorization = new("Bearer", config.SecretKey);
        using var getResponse = await http.SendAsync(getRequest, ct);
        if (!getResponse.IsSuccessStatusCode)
        {
            await LogFailureAsync("subscriptions.retrieve", getResponse, ct);
            return false;
        }

        using var doc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("items", out var items)
            || !items.TryGetProperty("data", out var data) || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("id", out var itemIdEl) || itemIdEl.GetString() is not { } itemId)
        {
            return false;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("items[0][id]", itemId),
            new("items[0][price]", newPriceId),
            new("proration_behavior", "create_prorations"),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/subscriptions/{subscriptionId}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new("Bearer", config.SecretKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync("subscriptions.update", response, ct);
            return false;
        }
        return true;
    }

    /// <summary>Open a Stripe billing-portal session for a customer and return its hosted URL, so an
    /// operator can manage the subscription (invoices, payment method, hard cancel, refunds) in Stripe's
    /// own UI. Null on failure.</summary>
    public async Task<string?> CreateBillingPortalSessionAsync(
        string customerId, string returnUrl, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("customer", customerId),
            new("return_url", returnUrl),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/billing_portal/sessions")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new("Bearer", config.SecretKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync("billing_portal.sessions.create", response, ct);
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
    }

    // Stripe returns a JSON error body ({"error":{"message":...}}) on a 4xx/5xx. Surface it at Error level and
    // self-report it as a Condux issue instead of letting it vanish behind the null/false return. The secret
    // key rides the Authorization header, not the body, so logging the body leaks nothing.
    private async Task LogFailureAsync(string operation, HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogError("Stripe {Operation} failed: HTTP {StatusCode} {Body}", operation, status, body);

        // Dogfood the failure into Condux's own error monitoring (#75). Throw-then-catch so the exception
        // carries a stack trace; caught here so the caller still returns its typed error. No-op when
        // CONDUX_SELF_DSN is unset, and the SDK never throws.
        try
        {
            throw new StripeApiException(operation, status);
        }
        catch (StripeApiException reportable)
        {
            selfReporter.Report(reportable);
        }
    }
}
