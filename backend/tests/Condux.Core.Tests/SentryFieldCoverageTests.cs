using System.Text.Json;
using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The capture contract against Sentry's documented event payload
/// (develop.sentry.dev/sdk/data-model/event-payloads, checked 2026-07-20). A full-fat fixture carries
/// every documented top-level field; each must be classified below as CAPTURED (and is then asserted on
/// the parsed Event) or EXCLUDED (with the reason). A field in the fixture that is in neither list fails
/// the build — so when a new Sentry field shows up, someone has to decide what we do with it, in code.
/// </summary>
public class SentryFieldCoverageTests
{
    // What the parser keeps on the normalized Event.
    private static readonly string[] Captured =
    [
        "event_id", "timestamp", "platform", "level", "logger", "transaction", "server_name",
        "release", "dist", "environment", "message", "tags", "extra", "fingerprint", "sdk",
        "exception", "breadcrumbs", "user", "request", "contexts", "modules",
    ];

    // What we deliberately do not keep, and why. Revisit deliberately, never by accident.
    private static readonly string[] Excluded =
    [
        "errors", // SDK-internal processing errors; no product surface
        "template", // legacy template-engine interface; effectively dead in modern SDKs
        "stacktrace", // event-level stack without an exception; revisit with threads (#81)
        "threads", // native/mobile thread dumps; revisit with tier-1 SDKs (#81)
        "debug_meta", // symbolication images; revisit with source-map/native support
        "type", // envelope item type, handled at the envelope layer
    ];

    // Every documented top-level field, with realistic values (privacy-excluded request fields included
    // on purpose: the test proves they are classified, the scrubber tests prove what happens to them).
    private const string FullFatEvent = """
    {
      "event_id": "bb6d8c0c43fc4630ad850ee518f1b9d0",
      "timestamp": 1789200000,
      "platform": "javascript",
      "level": "error",
      "logger": "app.checkout",
      "transaction": "POST /checkout",
      "server_name": "web-1",
      "release": "1.4.0",
      "dist": "build-42",
      "environment": "production",
      "message": { "formatted": "payment failed" },
      "tags": { "region": "eu" },
      "extra": { "attempt": "3" },
      "fingerprint": ["payment-failure"],
      "sdk": { "name": "sentry.javascript.browser", "version": "8.0.0" },
      "modules": { "stripe": "14.1.0", "react": "19.0.0" },
      "errors": [{ "type": "clock_drift" }],
      "template": { "filename": "index.html" },
      "stacktrace": { "frames": [] },
      "threads": { "values": [{ "id": 1, "crashed": true }] },
      "debug_meta": { "images": [] },
      "type": "event",
      "user": { "id": "user-7", "email": "wally@acme.io", "ip_address": "203.0.113.9" },
      "request": {
        "url": "https://shop.acme.io/checkout",
        "method": "POST",
        "query_string": "cart=9",
        "headers": { "User-Agent": "Mozilla/5.0" },
        "cookies": "session=abc",
        "data": { "card": "4242" },
        "env": { "REMOTE_ADDR": "203.0.113.9" }
      },
      "contexts": {
        "browser": { "name": "Chrome", "version": "126.0" },
        "os": { "name": "iOS", "version": "18.1" },
        "runtime": { "name": "node", "version": "22.1.0" },
        "device": { "model": "iPhone15,2" },
        "trace": { "trace_id": "0af7651916cd43dd8448eb211c80319c", "span_id": "b0e6f15b45c36b12" }
      },
      "exception": {
        "values": [{ "type": "PaymentError", "value": "declined", "mechanism": { "handled": true } }]
      },
      "breadcrumbs": {
        "values": [{
          "timestamp": 1789199990,
          "type": "http",
          "category": "fetch",
          "message": "POST /api/pay",
          "level": "info",
          "data": { "status_code": "502", "url": "/api/pay" }
        }]
      }
    }
    """;

    [Fact]
    public void Every_top_level_field_is_classified_captured_or_excluded()
    {
        using var doc = JsonDocument.Parse(FullFatEvent);
        var known = Captured.Concat(Excluded).ToHashSet();

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            Assert.True(
                known.Contains(property.Name),
                $"Unclassified Sentry field '{property.Name}': add it to Captured (and parse it) or to "
                + "Excluded (with the reason). Silent drops are not allowed.");
        }
    }

    [Fact]
    public void Captured_fields_actually_reach_the_parsed_event()
    {
        var e = SentryParser.ParseStore(FullFatEvent).Event!;

        Assert.Equal("bb6d8c0c43fc4630ad850ee518f1b9d0", e.EventId);
        Assert.Equal("app.checkout", e.Logger);
        Assert.Equal("POST /checkout", e.Transaction);
        Assert.Equal("web-1", e.ServerName);
        Assert.Equal("build-42", e.Dist);
        Assert.Equal("payment failed", e.Message);
        Assert.Equal("eu", e.Tags["region"]);
        Assert.Equal("3", e.Extra["attempt"]);
        Assert.Equal("payment-failure", Assert.Single(e.Fingerprint));
        Assert.Equal("14.1.0", e.Modules["stripe"]); // the "which lib version broke" map
        Assert.Equal("user-7", e.User!.Id);
        Assert.Equal("https://shop.acme.io/checkout", e.Request!.Url);
        Assert.Equal("Chrome 126.0", e.Contexts["browser"]);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", e.TraceId);
        Assert.True(Assert.Single(e.Exceptions).Handled);

        var crumb = Assert.Single(e.Breadcrumbs);
        Assert.Equal("502", crumb.Data["status_code"]); // structured crumb payload survives
        Assert.Equal("/api/pay", crumb.Data["url"]);
    }

    [Fact]
    public void Privacy_excluded_request_fields_never_reach_the_event()
    {
        var e = SentryParser.ParseStore(FullFatEvent).Event!;

        // Cookies, request bodies, and server env are excluded by policy at the parser, so they cannot
        // reach storage no matter what a scrubber does downstream.
        var serialized = JsonSerializer.Serialize(e);
        Assert.DoesNotContain("session=abc", serialized);
        Assert.DoesNotContain("4242", serialized);
        Assert.DoesNotContain("REMOTE_ADDR", serialized);
    }
}
