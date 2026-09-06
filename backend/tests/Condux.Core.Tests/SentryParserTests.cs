using Condux.Core.Events;
using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

public class SentryParserTests
{
    private const string PythonExceptionEvent = """
    {
      "event_id": "fc6d8c0c43fc4630ad850ee518f1b9d0",
      "timestamp": "2024-05-01T12:00:00Z",
      "platform": "python",
      "level": "error",
      "environment": "production",
      "release": "1.2.3",
      "sdk": { "name": "sentry.python", "version": "2.0.0" },
      "tags": { "server": "web-1" },
      "exception": {
        "values": [
          {
            "type": "ValueError",
            "value": "bad input",
            "stacktrace": {
              "frames": [
                {
                  "filename": "app.py", "function": "handle", "lineno": 42, "in_app": true,
                  "pre_context": ["def handle(order):", "    total = order.total"],
                  "context_line": "    charge(total.amount)",
                  "post_context": ["    return receipt"],
                  "vars": { "order": {"id": "ord_1"}, "total": "None" }
                }
              ]
            }
          }
        ]
      }
    }
    """;

    [Fact]
    public void CapturesAbsPathAndDebugMetaForSourceMaps()
    {
        // A bundled web SDK reports minified frames with abs_path (the built file URL) plus a debug_meta
        // image linking that code_file to a debug id — the keys source-map symbolication resolves on
        // (ADR-0028). Field names verified against Sentry's develop docs (debugmeta interface): a source-map
        // image has type "sourcemap", and its code_file corresponds to the frame's abs_path.
        const string payload = """
        {
          "event_id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "timestamp": 1714564800,
          "platform": "javascript",
          "level": "error",
          "release": "app@1.4.2",
          "exception": {
            "values": [
              {
                "type": "TypeError", "value": "x is undefined",
                "stacktrace": {
                  "frames": [
                    {
                      "filename": "app.min.js",
                      "abs_path": "https://app.example.com/js/app.min.js",
                      "function": "handleClick", "lineno": 1, "colno": 4823, "in_app": true
                    }
                  ]
                }
              }
            ]
          },
          "debug_meta": {
            "images": [
              {
                "type": "sourcemap",
                "code_file": "https://app.example.com/js/app.min.js",
                "debug_id": "1a2b3c4d-0000-0000-0000-000000000000"
              }
            ]
          }
        }
        """;

        var e = SentryParser.ParseStore(payload).Event!;

        var frame = Assert.Single(Assert.Single(e.Exceptions).Stacktrace!.Frames);
        Assert.Equal("https://app.example.com/js/app.min.js", frame.AbsPath);

        var image = Assert.Single(e.DebugImages);
        Assert.Equal("sourcemap", image.Type);
        Assert.Equal("https://app.example.com/js/app.min.js", image.CodeFile);
        Assert.Equal("1a2b3c4d-0000-0000-0000-000000000000", image.DebugId);
        // code_file lines up with the frame's abs_path, the match key symbolication uses.
        Assert.Equal(frame.AbsPath, image.CodeFile);
    }

    [Fact]
    public void ParsesStoreEvent()
    {
        var e = SentryParser.ParseStore(PythonExceptionEvent).Event!;

        Assert.Equal("fc6d8c0c43fc4630ad850ee518f1b9d0", e.EventId);
        Assert.Equal("python", e.Platform);
        Assert.Equal(Level.Error, e.Level);
        Assert.Equal("1.2.3", e.Release);
        Assert.Equal("sentry.python", e.SdkName);
        Assert.Equal("web-1", e.Tags["server"]);
        Assert.True(e.TimestampUnixMs > 0);

        var ex = Assert.Single(e.Exceptions);
        Assert.Equal("ValueError", ex.Type);
        Assert.NotNull(ex.Stacktrace);
        var frame = Assert.Single(ex.Stacktrace!.Frames);
        Assert.Equal("app.py", frame.Filename);
        Assert.Equal(42, frame.Lineno);
        Assert.True(frame.InApp);
        Assert.Equal(["def handle(order):", "    total = order.total"], frame.ContextBefore);
        Assert.Equal("    charge(total.amount)", frame.ContextLine);
        Assert.Equal(["    return receipt"], frame.ContextAfter);
        Assert.Equal("None", frame.Vars["total"]);
        Assert.Contains("ord_1", frame.Vars["order"]);
    }

    [Fact]
    public void ParsesEnvelope_FirstEventItem()
    {
        var envelope = string.Join('\n',
            """{"event_id":"abc"}""",
            """{"type":"event"}""",
            PythonExceptionEvent.ReplaceLineEndings(" "));

        var e = SentryParser.ParseEnvelope(envelope).Event!;

        Assert.NotNull(e);
        Assert.Equal(Level.Error, e!.Level);
    }

    // What Sentry SDKs send beyond the exception itself (#105) — asserted field by field, since the
    // transport suites cannot catch a wire-shape drift.
    private const string MetadataEvent = """
    {
      "event_id": "aa6d8c0c43fc4630ad850ee518f1b9d0",
      "timestamp": 1789200000,
      "platform": "javascript",
      "level": "error",
      "dist": "build-42",
      "user": {
        "id": "user-7",
        "username": "wally",
        "email": "wally@acme.io",
        "ip_address": "203.0.113.9"
      },
      "request": {
        "url": "https://shop.acme.io/checkout",
        "method": "POST",
        "query_string": "cart=9",
        "headers": { "User-Agent": "Mozilla/5.0", "Authorization": "Bearer xyz" }
      },
      "contexts": {
        "browser": { "name": "Chrome", "version": "126.0" },
        "os": { "name": "iOS", "version": "18.1" },
        "runtime": { "name": "node", "version": "22.1.0" },
        "device": { "model": "iPhone15,2" },
        "trace": { "trace_id": "0af7651916cd43dd8448eb211c80319c" }
      },
      "exception": {
        "values": [
          {
            "type": "TypeError",
            "value": "boom",
            "mechanism": { "type": "onerror", "handled": false }
          }
        ]
      }
    }
    """;

    [Fact]
    public void ParsesUserRequestContextsAndMechanism()
    {
        var e = SentryParser.ParseStore(MetadataEvent).Event!;

        Assert.Equal("user-7", e.User!.Id);
        Assert.Equal("wally", e.User.Username);
        Assert.Equal("wally@acme.io", e.User.Email);
        Assert.Equal("203.0.113.9", e.User.IpAddress);

        Assert.Equal("https://shop.acme.io/checkout", e.Request!.Url);
        Assert.Equal("POST", e.Request.Method);
        Assert.Equal("cart=9", e.Request.QueryString);
        Assert.Equal("Mozilla/5.0", e.Request.Headers["User-Agent"]);

        Assert.Equal("Chrome 126.0", e.Contexts["browser"]);
        Assert.Equal("iOS 18.1", e.Contexts["os"]);
        Assert.Equal("node 22.1.0", e.Contexts["runtime"]);
        Assert.Equal("iPhone15,2", e.Contexts["device"]);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", e.TraceId);
        Assert.Equal("build-42", e.Dist);

        Assert.False(Assert.Single(e.Exceptions).Handled); // the unhandled-crash badge
    }

    [Fact]
    public void MissingMetadataStaysNullNotFabricated()
    {
        var e = SentryParser.ParseStore(PythonExceptionEvent).Event!;

        Assert.Null(e.User);
        Assert.Null(e.Request);
        Assert.Empty(e.Contexts);
        Assert.Null(e.TraceId);
        Assert.Null(Assert.Single(e.Exceptions).Handled); // no mechanism reported = unknown, not false
    }

    /// <summary>
    /// The runtime dependency inventory arriving from a real SDK (ADR-0041). The body below was
    /// captured verbatim from @condux/node rather than written here, because the failure this guards
    /// against is the two sides disagreeing about the wire, and a payload composed by hand in this
    /// file would agree with the parser by construction.
    ///
    /// Nothing covered `modules` before this, despite the parser having read it since it was written.
    /// A rename or a nesting change on either side would have produced an empty inventory and an
    /// exposure surface reporting "not observed" for everything, with no test going red.
    /// </summary>
    private const string NodeEventWithModules = """
    {
      "event_id": "687f28e20a1d497baec92ea7a8a11699",
      "timestamp": 1788514972.082,
      "platform": "javascript",
      "environment": "production",
      "release": "1.4.2",
      "modules": { "@acme/widgets": "2.1.0", "jsonwebtoken": "8.5.1", "lodash": "4.17.11" },
      "level": "error",
      "exception": {
        "values": [
          {
            "type": "Error",
            "value": "boom",
            "mechanism": { "type": "generic", "handled": true },
            "stacktrace": { "frames": [] }
          }
        ]
      }
    }
    """;

    [Fact]
    public void ParsesTheRuntimeModuleInventory()
    {
        var e = SentryParser.ParseStore(NodeEventWithModules).Event!;

        Assert.Equal("4.17.11", e.Modules["lodash"]);
        Assert.Equal("2.1.0", e.Modules["@acme/widgets"]);
        // The package whose name matches the scrub's sensitive-key patterns. It is the most valuable
        // entry the inventory can carry, and it must arrive as a version rather than as a redaction.
        Assert.Equal("8.5.1", e.Modules["jsonwebtoken"]);
    }

    [Fact]
    public void AnEventWithoutModulesReportsAnEmptyInventoryRatherThanNull()
    {
        var e = SentryParser.ParseStore(PythonExceptionEvent).Event!;

        Assert.Empty(e.Modules);
    }
}
