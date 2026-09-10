using System.Net;
using Xunit;

namespace Condux.Sdk.Tests;

// Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes. The cases live in that
// file rather than here so the seven transports assert against one artifact instead of seven readings of
// one sentence in a comment. See the file for why it is data and not prose.
//
// ScriptedTransport.RateLimited takes an int and builds a typed RetryConditionHeaderValue, so it
// cannot express a fractional or a malformed Retry-After at all. RawRetryAfter below sets the header
// as a string, the way a relay does, which is the only way to reach those cases from a test.
public class BackoffConformanceTests
{
    private const string Dsn = "https://k@ingest.example.test/p";

    public static TheoryData<int, int, string?, int> Cases()
    {
        var data = new TheoryData<int, int, string?, int>();
        var count = 0;
        foreach (var line in File.ReadAllLines(FixturePath()))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            var columns = text.Split('\t');
            var retryAfter = columns[2] switch { "<none>" => null, "<empty>" => "", var v => v };
            data.Add(int.Parse(columns[0]), int.Parse(columns[1]), retryAfter, int.Parse(columns[3]));
            count++;
        }

        // A fixture that failed to load reads exactly like one where every case passed, so the count is
        // asserted rather than assumed. A floor, so adding a case does not mean editing seven SDKs.
        Assert.True(count >= 15, $"backoff.tsv looks truncated: {count} case(s)");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Waits_exactly_as_long_as_the_fleet_contract_says(
        int attempt, int status, string? retryAfter, int expectedMs)
    {
        var delays = new List<TimeSpan>();
        var client = new ConduxClient(new ConduxOptions
        {
            Dsn = Dsn,
            // One more retry than the attempt under test, so the sleep that follows it is recorded.
            MaxRetries = attempt + 1,
            Transport = new ScriptedTransport(RawRetryAfter((HttpStatusCode)status, retryAfter)),
            Sleep = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        });

        await client.CaptureMessageAsync("hi");

        Assert.Equal(attempt + 1, delays.Count);
        Assert.Equal(expectedMs, (int)delays[attempt].TotalMilliseconds);
    }

    // The failing response, repeated, carrying Retry-After as a raw string rather than a parsed value.
    private static Func<HttpResponseMessage> RawRetryAfter(HttpStatusCode status, string? retryAfter) =>
        () =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        };

    // The fixture is shared across the fleet, so it sits above this SDK rather than inside it. Walking up
    // for it beats a relative hop from the test binary, which changes with the target framework.
    private static string FixturePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sdks", "conformance", "backoff.tsv");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("sdks/conformance/backoff.tsv not found above the test binary.");
    }
}
