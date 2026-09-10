using Condux.Agent;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The helper had no tests at all, which is how an unbounded field survived in it. What it returns goes
/// into an exception message and then into the run's audit row, so both what it says and how much of it
/// it says are worth pinning.
/// </summary>
public class ModelApiErrorTests
{
    [Fact]
    public void Describes_an_openai_error_envelope_with_its_type()
    {
        var described = ModelApiError.Describe(
            """{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""");

        Assert.Equal("invalid_request_error: Incorrect API key provided", described);
    }

    [Fact]
    public void Describes_an_envelope_with_no_type_using_the_message_alone()
    {
        var described = ModelApiError.Describe("""{"error":{"message":"invalid x-api-key"}}""");

        Assert.Equal("invalid x-api-key", described);
    }

    /// <summary>
    /// The defect this file was written for. The raw fallback was capped and the envelope's message was
    /// not, so a body shaped like an error envelope wrote its whole message into a JSONB column, once per
    /// failed run. Removing the cap makes this fail; the assertion is on the content and not the length,
    /// because a length check would also hold against 500 characters of something else entirely.
    /// </summary>
    [Fact]
    public void Bounds_the_message_of_an_envelope_as_well_as_a_raw_body()
    {
        var described = ModelApiError.Describe(
            "{\"error\":{\"message\":\"" + new string('x', 900) + "\"}}");

        Assert.Equal(new string('x', 500), described);
    }

    [Fact]
    public void Bounds_a_body_it_cannot_parse()
    {
        Assert.Equal(new string('y', 500), ModelApiError.Describe(new string('y', 900)));
    }

    /// <summary>
    /// A body that is not an envelope falls back to itself, by either route: a JsonException for
    /// something that is not JSON, or the shape check failing for JSON that is. Only the first passes
    /// through a catch block, which is why both are here.
    /// </summary>
    [Theory]
    [InlineData("<html><body>upstream timeout</body></html>")]
    [InlineData("""{"data":[{"id":"gpt-x"}]}""")]
    [InlineData("""{"error":"a string, not an object"}""")]
    [InlineData("""{"error":{"message":["not a string"]}}""")]
    public void Falls_back_to_the_body_when_it_is_not_an_error_envelope(string body)
    {
        Assert.Equal(body, ModelApiError.Describe(body));
    }

    /// <summary>This runs while reporting a failure, so throwing here would replace the real problem with
    /// a complaint about the shape of the evidence.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("null")]
    public void Never_throws_on_a_body_it_cannot_read(string body)
    {
        Assert.Equal(body, ModelApiError.Describe(body));
    }
}
