using Condux.Core.Events;
using Condux.Core.Text;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// An event's JSON form holds no U+0000. Postgres <c>text</c> refuses the character, and a NUL in an
/// exception message reached the issue upsert in the consumer, whose drain loop then pauses before the
/// next message, so one sender could slow ingest for every tenant. The character is replaced, keeping the
/// event, in every string the event carries, dictionary keys included.
/// </summary>
public class EventJsonTests
{
    private static readonly Event WithNul = new()
    {
        EventId = "e\0",
        Message = "m\0",
        Release = "1.0\0",
        Tags = new Dictionary<string, string> { ["k\0"] = "v\0" },
        Exceptions =
        [
            new ExceptionValue
            {
                Type = "Type\0Error",
                Value = "bad\0value",
                Stacktrace = new Stacktrace
                {
                    Frames = [new Frame { Function = "f\0", Filename = "a\0.js", Module = "mod\0" }],
                },
            },
        ],
    };

    [Fact]
    public void Publishing_replaces_every_nul_with_the_replacement_character()
    {
        var json = EventJson.ToJson(WithNul);

        Assert.DoesNotContain("\\u0000", json, StringComparison.OrdinalIgnoreCase);
        var e = EventJson.ToEvent(json)!;
        Assert.Equal("Type\uFFFDError", e.Exceptions[0].Type);
        Assert.Equal("bad\uFFFDvalue", e.Exceptions[0].Value);
        Assert.Equal("f\uFFFD", e.Exceptions[0].Stacktrace!.Frames[0].Function);
        Assert.Equal("v\uFFFD", e.Tags["k\uFFFD"]);
    }

    /// <summary>A message published before the fix, or by an older relay during a deploy, still carries
    /// the escape; reading it replaces the character too, as does reading an event stored before.</summary>
    [Fact]
    public void Reading_replaces_a_nul_the_message_already_carries()
    {
        var e = EventJson.ToEvent("""{"Message":"a\u0000b","Tags":{"k\u0000":"v"}}""")!;

        Assert.Equal("a\uFFFDb", e.Message);
        Assert.True(e.Tags.ContainsKey("k\uFFFD"));
    }

    [Fact]
    public void Text_without_a_nul_is_returned_as_it_is()
    {
        const string text = "plain text";

        Assert.Same(text, StorableText.ToStorable(text));
    }
}
