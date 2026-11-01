using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Values of the wrong JSON kind where the parser expects an object: an array item that is a number, a
/// string or null. Looking a member up on anything but an object throws, and the breadcrumb, exception and
/// frame cases escaped the parser as a 500; the thread, image and context cases were already guarded and
/// are here so every place an item is read has one. The parser reads loosely-typed fields with defaults so
/// one odd field never loses the event, so the event still parses and only the odd item is skipped.
/// </summary>
public class SentryWrongKindTests
{
    [Theory]
    [InlineData("""{"message":"m","breadcrumbs":[1]}""")]
    [InlineData("""{"message":"m","breadcrumbs":{"values":["x",null]}}""")]
    [InlineData("""{"message":"m","exception":[1]}""")]
    [InlineData("""{"message":"m","exception":{"values":[null]}}""")]
    [InlineData("""{"message":"m","exception":[{"type":"E","stacktrace":{"frames":[1,"f",null]}}]}""")]
    [InlineData("""{"message":"m","threads":{"values":[1]}}""")]
    [InlineData("""{"message":"m","debug_meta":{"images":[1,null]}}""")]
    [InlineData("""{"message":"m","contexts":{"os":1,"trace":[]}}""")]
    public void A_value_of_the_wrong_kind_is_skipped_and_the_event_parses(string json)
    {
        var result = SentryParser.ParseStore(json);

        Assert.Equal(ParseOutcome.Parsed, result.Outcome);
        Assert.Equal("m", result.Event!.Message);
    }
}
