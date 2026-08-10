using System.Text.Json;

namespace Condux.Core.Billing;

/// <summary>
/// The fields we care about from a Stripe webhook event, flattened out of the nested <c>data.object</c>.
/// Which fields are populated depends on <see cref="Type"/> — a checkout session carries the
/// <see cref="ClientReferenceId"/> (our org id) that links a Stripe customer to an org; subscription
/// events carry the <see cref="PriceId"/> that maps to a plan tier.
/// </summary>
public sealed record StripeEvent(
    string Type,
    string? ClientReferenceId,
    string? CustomerId,
    string? SubscriptionId,
    string? PriceId,
    string? Status,
    string? TierName = null);

/// <summary>
/// Parses a Stripe webhook event body into a <see cref="StripeEvent"/>. Pure, dependency-free (System.Text.
/// Json only), so it is unit-tested in CI without the Stripe SDK. Only the event types the billing flow
/// acts on need to be understood; unknown shapes still parse (with null fields) and are ignored downstream.
/// </summary>
public static class StripeEvents
{
    public static StripeEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = Str(root, "type");
            if (type is null || !root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("object", out var obj))
            {
                return null;
            }

            // A checkout.session carries the subscription id under "subscription"; a subscription object
            // carries its own id under "id" and its price under items.data[0].price.id.
            var subscriptionId = Str(obj, "subscription") ?? (IsSubscription(obj) ? Str(obj, "id") : null);

            return new StripeEvent(
                Type: type,
                ClientReferenceId: Str(obj, "client_reference_id"),
                CustomerId: Str(obj, "customer"),
                SubscriptionId: subscriptionId,
                PriceId: FirstItemPriceId(obj),
                Status: Str(obj, "status"),
                // We stamp the purchased tier on the checkout session's metadata so the completed-checkout
                // handler can set the tier itself, independent of the subscription-event delivery order.
                TierName: Metadata(obj, "condux_tier"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSubscription(JsonElement obj) =>
        Str(obj, "object") == "subscription";

    // A value from the object's metadata map (e.g. the condux_tier we set on the checkout session).
    private static string? Metadata(JsonElement obj, string key) =>
        obj.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
            ? Str(md, key)
            : null;

    // items.data[0].price.id on a subscription object.
    private static string? FirstItemPriceId(JsonElement obj)
    {
        if (obj.TryGetProperty("items", out var items)
            && items.TryGetProperty("data", out var arr)
            && arr.ValueKind == JsonValueKind.Array
            && arr.GetArrayLength() > 0
            && arr[0].TryGetProperty("price", out var price))
        {
            return Str(price, "id");
        }

        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
