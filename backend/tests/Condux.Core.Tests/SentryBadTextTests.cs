using System.Text;
using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Well formed JSON whose strings are not text: an escape for half a surrogate pair, or a byte that is not
/// UTF-8. The JSON parser accepts such a document and the runtime throws only when a string or a member
/// name is read, which used to escape the parser and answer the request with a 500 the relay then filed
/// against itself. Each is the sender's mistake, so it is a malformed payload.
/// </summary>
public class SentryBadTextTests
{
    [Theory]
    [InlineData("""{"message":"\uD800"}""")]
    [InlineData("""{"\uD800":1,"message":"a"}""")]
    [InlineData("""{"tags":{"\uDC00":"x"}}""")]
    [InlineData("""{"exception":{"values":[{"type":"E","value":"\uDBFF"}]}}""")]
    public void A_store_payload_whose_text_is_not_text_is_malformed(string json)
    {
        Assert.Equal(ParseOutcome.Malformed, SentryParser.ParseStore(json).Outcome);
    }

    /// <summary>The byte alone, and beside an escape, where the string is unescaped before it is checked.</summary>
    [Theory]
    [InlineData("""{"message":"a"}""")]
    [InlineData("""{"message":"éa"}""")]
    public void A_store_payload_holding_a_byte_that_is_not_utf8_is_malformed(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        bytes[Array.LastIndexOf(bytes, (byte)'a')] = 0xFF;

        Assert.Equal(ParseOutcome.Malformed, SentryParser.ParseStore(bytes).Outcome);
    }

    [Theory]
    [InlineData("""{"event_id":"e"}""" + "\n" + """{"type":"event"}""" + "\n" + """{"message":"\uD800"}""")]
    [InlineData("""{"sent_at":"\uD800"}""" + "\n" + """{"type":"event"}""" + "\n" + """{"message":"a"}""")]
    [InlineData("""{}""" + "\n" + """{"type":"\uD800"}""" + "\n" + """{}""" + "\n" + """{"type":"event"}""" + "\n" + """{"message":"a"}""")]
    public void An_envelope_whose_text_is_not_text_is_malformed(string envelope)
    {
        Assert.Equal(ParseOutcome.Malformed, SentryParser.ParseEnvelope(envelope).Outcome);
    }

    /// <summary>Escaped text that is valid, a whole surrogate pair included, still reads as written.</summary>
    [Fact]
    public void Valid_escaped_text_still_parses()
    {
        var result = SentryParser.ParseStore("""{"message":"café 😀","tags":{"ké":"v"}}""");

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal("café 😀", result.Event!.Message);
        Assert.Equal("v", result.Event.Tags["ké"]);
    }
}
