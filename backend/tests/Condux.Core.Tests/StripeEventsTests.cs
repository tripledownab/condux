using Condux.Core.Billing;
using Xunit;

namespace Condux.Core.Tests;

public class StripeEventsTests
{
    [Fact]
    public void Parse_checkout_session_completed_extracts_the_org_link()
    {
        const string json = """
        {
          "type": "checkout.session.completed",
          "data": { "object": {
            "object": "checkout.session",
            "client_reference_id": "42",
            "customer": "cus_ABC",
            "subscription": "sub_123",
            "metadata": { "condux_tier": "Business" }
          } }
        }
        """;

        var e = StripeEvents.Parse(json);

        Assert.NotNull(e);
        Assert.Equal("checkout.session.completed", e!.Type);
        Assert.Equal("42", e.ClientReferenceId); // the org id we passed as client_reference_id
        Assert.Equal("cus_ABC", e.CustomerId);
        Assert.Equal("sub_123", e.SubscriptionId);
        // The tier we stamp on the session drives the order-independent tier apply in the webhook handler.
        Assert.Equal("Business", e.TierName);
    }

    [Fact]
    public void Parse_subscription_updated_extracts_price_and_status()
    {
        const string json = """
        {
          "type": "customer.subscription.updated",
          "data": { "object": {
            "object": "subscription",
            "id": "sub_123",
            "customer": "cus_ABC",
            "status": "active",
            "items": { "data": [ { "price": { "id": "price_business" } } ] }
          } }
        }
        """;

        var e = StripeEvents.Parse(json);

        Assert.NotNull(e);
        Assert.Equal("customer.subscription.updated", e!.Type);
        Assert.Equal("cus_ABC", e.CustomerId);
        Assert.Equal("sub_123", e.SubscriptionId); // from the subscription's own id
        Assert.Equal("price_business", e.PriceId);
        Assert.Equal("active", e.Status);
    }

    [Fact]
    public void Parse_ignores_an_unknown_event_shape_without_throwing()
    {
        var e = StripeEvents.Parse("""{"type":"invoice.paid","data":{"object":{"object":"invoice"}}}""");
        Assert.NotNull(e);
        Assert.Equal("invoice.paid", e!.Type);
        Assert.Null(e.PriceId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"type":"x"}""")]
    public void Parse_returns_null_for_malformed_or_incomplete_events(string json) =>
        Assert.Null(StripeEvents.Parse(json));
}
