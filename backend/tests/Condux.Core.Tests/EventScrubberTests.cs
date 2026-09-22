using System.Security.Cryptography;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Scrub;
using Xunit;

namespace Condux.Core.Tests;

public class EventScrubberTests
{
    // A project's user-key salt. Any non-empty value does, since what these assert is that the salt is
    // USED, never that it produces one particular digest.
    private const string Salt = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ScrubsMessageExceptionsAndSensitiveTags()
    {
        var e = new Event
        {
            Message = "user ada@example.com failed",
            Exceptions = [new ExceptionValue { Type = "Err", Value = "token sk-ABCDEF0123456789 leaked" }],
            Tags = new Dictionary<string, string> { ["authorization"] = "Bearer xyz", ["env"] = "prod" },
        };

        var scrubbed = EventScrubber.Scrub(e, Salt);

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

        var frame = EventScrubber.Scrub(e, Salt).Exceptions[0].Stacktrace!.Frames[0];

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

        var scrubbed = EventScrubber.Scrub(e, Salt);

        // The pseudonymous counting key is derived from the raw identifiers before they are removed,
        // and the same user always derives the same key (distinct counts work across events).
        Assert.Equal(32, scrubbed.UserKey.Length);
        Assert.Equal(scrubbed.UserKey, EventScrubber.Scrub(e, Salt).UserKey);
        // What reaches storage: id/username stay, the email is redacted, the IP is gone entirely.
        Assert.Equal("user-7", scrubbed.User!.Id);
        Assert.Equal("wally", scrubbed.User.Username);
        Assert.Equal("[redacted]", scrubbed.User.Email);
        Assert.Null(scrubbed.User.IpAddress);
    }

    [Fact]
    public void TwoProjectsDeriveDifferentKeysForTheSamePerson()
    {
        // The whole point of the salt. One address reported to two projects must not produce one key,
        // or a table built from one project's events re-identifies the other's. Fails if Derive ignores
        // the salt it is handed, which is the only way this regresses.
        var user = new EventUser { Email = "wally@acme.io" };

        var first = UserKeys.Derive(user, "salt-of-project-one");
        var second = UserKeys.Derive(user, "salt-of-project-two");

        Assert.NotEqual(first, second);
        Assert.Equal(32, first.Length);
        Assert.Equal(32, second.Length);
        // Stable within a project, or "users affected" counts one person once per event.
        Assert.Equal(first, UserKeys.Derive(user, "salt-of-project-one"));
    }

    [Fact]
    public void TheKeyIsTheKeyedHashAndNotTheBareOne()
    {
        // Two assertions doing different jobs. The first pins the CONSTRUCTION: it recomputes what the
        // implementation should produce, so swapping HMAC for anything else fails here rather than
        // passing quietly. On its own an inequality would not do that, since "not the old hash" is
        // satisfied by any change at all, good or bad. The second pins the SPECIFIC regression, which
        // is reverting to the unkeyed hash. An IP is the identifier that makes this matter, because the
        // whole v4 address space is small enough to work through from end to end.
        const string ip = "203.0.113.9";
        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes(ip)))[..32];
        var bare = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ip)))[..32];

        var derived = UserKeys.Derive(new EventUser { IpAddress = ip }, Salt);

        Assert.Equal(expected, derived);
        Assert.NotEqual(bare, derived);
    }

    [Fact]
    public void AnIncomingUserKeyIsOverwrittenRatherThanTrusted()
    {
        // No parser sets UserKey today, so this guards the day one does. The scrub must derive its own,
        // or whoever sends the event picks the pseudonym: they could bury a project's counts under one
        // key, claim another person's, or simply hand over the unkeyed value the salt exists to stop.
        var e = new Event
        {
            UserKey = "deadbeefdeadbeefdeadbeefdeadbeef",
            User = new EventUser { Id = "user-7" },
        };

        var scrubbed = EventScrubber.Scrub(e, Salt);

        Assert.NotEqual("deadbeefdeadbeefdeadbeefdeadbeef", scrubbed.UserKey);
        Assert.Equal(UserKeys.Derive(e.User, Salt), scrubbed.UserKey);
    }

    [Fact]
    public void AnEmptySaltIsRefusedRatherThanDerivedWith()
    {
        // An empty HMAC key still hashes, so the quiet failure here is a key that looks derived and is
        // as reversible as the one this replaced. An event carrying no user is still fine, because it
        // needs no salt to answer.
        Assert.Throws<ArgumentException>(() => UserKeys.Derive(new EventUser { Id = "u1" }, ""));
        Assert.Equal("", UserKeys.Derive(null, ""));
    }

    [Fact]
    public void AnonymousUsersFallBackToTheIpForTheKeyWithoutStoringIt()
    {
        var scrubbed = EventScrubber.Scrub(new Event
        {
            User = new EventUser { IpAddress = "203.0.113.9" },
        }, Salt);

        Assert.Equal(32, scrubbed.UserKey.Length); // still countable as one distinct user
        Assert.Null(scrubbed.User!.IpAddress); // but the IP itself never reaches storage
        Assert.Equal("", EventScrubber.Scrub(new Event(), Salt).UserKey); // no user at all = no key
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

        var scrubbed = EventScrubber.Scrub(e, Salt);

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

        var scrubbed = EventScrubber.Scrub(e, Salt);

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

        var scrubbed = EventScrubber.Scrub(e, Salt);

        Assert.Equal("[redacted]", scrubbed.Modules["some-pkg"]);
        Assert.Equal("built by [redacted]", scrubbed.Modules["other-pkg"]);
    }
}
