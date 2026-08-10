using Condux.Core.Events;
using Condux.Core.Scrub;
using Xunit;

namespace Condux.Core.Tests;

public class EventScrubberTests
{
    [Fact]
    public void ScrubsMessageExceptionsAndSensitiveTags()
    {
        var e = new Event
        {
            Message = "user ada@example.com failed",
            Exceptions = [new ExceptionValue { Type = "Err", Value = "token sk-ABCDEF0123456789 leaked" }],
            Tags = new Dictionary<string, string> { ["authorization"] = "Bearer xyz", ["env"] = "prod" },
        };

        var scrubbed = EventScrubber.Scrub(e);

        Assert.Equal("user [redacted] failed", scrubbed.Message);
        Assert.Equal("token [redacted] leaked", scrubbed.Exceptions[0].Value);
        Assert.Equal("[redacted]", scrubbed.Tags["authorization"]);
        Assert.Equal("prod", scrubbed.Tags["env"]);
    }

    [Fact]
    public void ScrubsFrameContextAndLocals()
    {
        var e = new Event
        {
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "Err",
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame
                            {
                                Filename = "pay.ts",
                                ContextBefore = ["const key = \"sk-ABCDEF0123456789\";"],
                                ContextLine = "charge(key, total);",
                                ContextAfter = ["return receipt;"],
                                Vars = new Dictionary<string, string>
                                {
                                    ["password"] = "hunter2",
                                    ["total"] = "None",
                                },
                            },
                        ],
                    },
                },
            ],
        };

        var frame = EventScrubber.Scrub(e).Exceptions[0].Stacktrace!.Frames[0];

        Assert.Equal("const key = \"[redacted]\";", frame.ContextBefore[0]);
        Assert.Equal("return receipt;", frame.ContextAfter[0]);
        Assert.Equal("[redacted]", frame.Vars["password"]);
        Assert.Equal("None", frame.Vars["total"]);
    }

    [Fact]
    public void DerivesTheUserKeyThenStripsRawIdentifiers()
    {
        var e = new Event
        {
            User = new EventUser
            {
                Id = "user-7",
                Username = "wally",
                Email = "wally@acme.io",
                IpAddress = "203.0.113.9",
            },
        };

        var scrubbed = EventScrubber.Scrub(e);

        // The pseudonymous counting key is derived from the raw identifiers before they are removed,
        // and the same user always derives the same key (distinct counts work across events).
        Assert.Equal(32, scrubbed.UserKey.Length);
        Assert.Equal(scrubbed.UserKey, EventScrubber.Scrub(e).UserKey);
        // What reaches storage: id/username stay, the email is redacted, the IP is gone entirely.
        Assert.Equal("user-7", scrubbed.User!.Id);
        Assert.Equal("wally", scrubbed.User.Username);
        Assert.Equal("[redacted]", scrubbed.User.Email);
        Assert.Null(scrubbed.User.IpAddress);
    }

    [Fact]
    public void AnonymousUsersFallBackToTheIpForTheKeyWithoutStoringIt()
    {
        var scrubbed = EventScrubber.Scrub(new Event
        {
            User = new EventUser { IpAddress = "203.0.113.9" },
        });

        Assert.Equal(32, scrubbed.UserKey.Length); // still countable as one distinct user
        Assert.Null(scrubbed.User!.IpAddress); // but the IP itself never reaches storage
        Assert.Equal("", EventScrubber.Scrub(new Event()).UserKey); // no user at all = no key
    }

    [Fact]
    public void ScrubsRequestAndContexts()
    {
        var e = new Event
        {
            Request = new RequestInfo
            {
                Url = "https://shop.acme.io/checkout?email=ada@example.com",
                QueryString = "email=ada@example.com",
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer xyz",
                    ["User-Agent"] = "Mozilla/5.0",
                },
            },
            Contexts = new Dictionary<string, string> { ["browser"] = "Chrome 126.0" },
        };

        var scrubbed = EventScrubber.Scrub(e);

        Assert.DoesNotContain("ada@example.com", scrubbed.Request!.Url);
        Assert.DoesNotContain("ada@example.com", scrubbed.Request.QueryString);
        Assert.Equal("[redacted]", scrubbed.Request.Headers["Authorization"]);
        Assert.Equal("Mozilla/5.0", scrubbed.Request.Headers["User-Agent"]);
        Assert.Equal("Chrome 126.0", scrubbed.Contexts["browser"]);
    }
}
