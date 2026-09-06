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

    /// <summary>
    /// The sensitive key rule applies where the key is a field name and not where it is a package name
    /// (ADR-0041). Both halves are asserted together, because the rule is right for tags and wrong for
    /// modules for the same reason, and a change that "fixes" one by breaking the other must fail here.
    /// </summary>
    [Fact]
    public void KeepsModuleVersionsForCredentialNamedPackagesButStillRedactsCredentialNamedTags()
    {
        var e = new Event
        {
            Modules = new Dictionary<string, string>
            {
                // Every one of these matches Scrubber.IsSensitiveKey. All are real packages, and
                // jsonwebtoken is the one that decides this test: widely installed, repeatedly
                // advisory-bearing, and therefore the row a CVE exposure read most needs to be correct.
                ["jsonwebtoken"] = "9.0.0",
                ["csrf-token"] = "1.0.2",
                ["secretbox"] = "0.4.1",
                ["tokenizer"] = "2.1.0",
                ["lodash"] = "4.17.11",
            },
            Tags = new Dictionary<string, string> { ["api_key"] = "sk-live-must-not-survive" },
        };

        var scrubbed = EventScrubber.Scrub(e);

        Assert.Equal("9.0.0", scrubbed.Modules["jsonwebtoken"]);
        Assert.Equal("1.0.2", scrubbed.Modules["csrf-token"]);
        Assert.Equal("0.4.1", scrubbed.Modules["secretbox"]);
        Assert.Equal("2.1.0", scrubbed.Modules["tokenizer"]);
        Assert.Equal("4.17.11", scrubbed.Modules["lodash"]);

        // The exemption is scoped to the key rule, not to scrubbing. A tag named after a credential
        // still loses its value, so this test fails if the exemption is ever widened past Modules.
        Assert.Equal("[redacted]", scrubbed.Tags["api_key"]);
    }

    /// <summary>
    /// Exempting Modules from the key rule must not exempt it from the value scrub. Nothing constrains
    /// what a client puts in this map, so the string scrub still runs over every value.
    ///
    /// Note what this test is for. It passes both before and after the key rule exemption, because
    /// neither key here matches <c>IsSensitiveKey</c>, so it does not prove the exemption. It guards a
    /// different and plausible mistake: reading "exempt Modules from the key rule" as "exempt Modules
    /// from scrubbing". The test above is the one that proves the exemption itself.
    /// </summary>
    [Fact]
    public void StillScrubsTokenShapedModuleValues()
    {
        var e = new Event
        {
            Modules = new Dictionary<string, string>
            {
                ["some-pkg"] = "sk-ABCDEF0123456789",
                ["other-pkg"] = "built by ada@example.com",
            },
        };

        var scrubbed = EventScrubber.Scrub(e);

        Assert.Equal("[redacted]", scrubbed.Modules["some-pkg"]);
        Assert.Equal("built by [redacted]", scrubbed.Modules["other-pkg"]);
    }
}
