using System.Net;
using Condux.Storage.ClickHouse;
using Xunit;

namespace Condux.ControlPlane.Tests;

/// <summary>
/// Covers what hand-running the SQL against a real ClickHouse cannot: that the C# side builds the
/// request it means to and reads the response back into the right fields. The query itself was checked
/// against ClickHouse 24.8 and is covered end to end by the integration suite.
///
/// Lives here rather than in a Storage test project because the control-plane is this reader's only
/// consumer, matching how the ClickHouse writers are covered from the consumer's test project.
/// </summary>
public class ClickHouseReleaseModuleReaderTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string Query { get; private set; } = "";

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    // Shaped exactly as ClickHouse answers the reader's query under FORMAT JSON: the aggregate columns
    // arrive under their SQL aliases, and toUnixTimestamp lands as a bare number rather than a string.
    private const string OneRow =
        """{"data":[{"ecosystem":"npm","package":"lodash","version":"4.17.11","environment":"production","latest_release":"1.4.2","seen":1788508800}]}""";

    private static (ClickHouseReleaseModuleReader Reader, StubHandler Handler) Build(string json = OneRow)
    {
        var handler = new StubHandler(json);
        return (
            new ClickHouseReleaseModuleReader(
                new HttpClient(handler) { BaseAddress = new Uri("http://clickhouse") }),
            handler);
    }

    /// <summary>
    /// Every column, and in particular the two whose JSON key is not simply the property name. The
    /// release arrives as <c>latest_release</c> because the query aliases argMax, and the time arrives
    /// as unix seconds. Rename either side and nothing else in the build would notice.
    /// </summary>
    [Fact]
    public async Task ReadsEveryColumnIncludingTheAliasedReleaseAndTheUnixTime()
    {
        var (reader, _) = Build();

        var observed = Assert.Single(await reader.ObservedAsync("7", ["lodash"]));

        Assert.Equal("npm", observed.Ecosystem);
        Assert.Equal("lodash", observed.Package);
        Assert.Equal("4.17.11", observed.Version);
        Assert.Equal("production", observed.Environment);
        Assert.Equal("1.4.2", observed.Release);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788508800), observed.LastSeen);
    }

    /// <summary>
    /// The query filters on <c>lower(package)</c>, so a name sent in its original casing would match
    /// nothing and the finding would read as "not observed". Blanks are dropped and repeats collapse,
    /// since a findings list routinely names one package several times.
    /// </summary>
    [Fact]
    public async Task LowersTrimsAndDeduplicatesTheRequestedNames()
    {
        var (reader, handler) = Build();

        await reader.ObservedAsync("7", ["Flask", "flask", "  Django  ", "   "]);

        Assert.Contains("param_packages=['flask','django']", handler.Query);
    }

    /// <summary>
    /// Package names come from a scanner rather than from us, and the array parameter is a quoted
    /// literal, so a name carrying a quote or a backslash must not be able to end an element early.
    /// </summary>
    [Fact]
    public async Task EscapesANameThatCouldTerminateTheArrayLiteral()
    {
        var (reader, handler) = Build();

        await reader.ObservedAsync("7", ["it's", @"back\slash"]);

        Assert.Contains(@"param_packages=['it\'s','back\\slash']", handler.Query);
    }

    [Fact]
    public async Task ScopesTheReadToTheProject()
    {
        var (reader, handler) = Build();

        await reader.ObservedAsync("7", ["lodash"]);

        Assert.Contains("param_pid=7", handler.Query);
    }

    [Fact]
    public async Task AsksClickHouseNothingWhenNoPackagesWereNamed()
    {
        var (reader, handler) = Build();

        Assert.Empty(await reader.ObservedAsync("7", []));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>An empty result is empty, not an error. It means unknown, which the caller decides.</summary>
    [Fact]
    public async Task ReturnsNothingWhenTheProjectHasNoInventory()
    {
        var (reader, _) = Build("""{"data":[]}""");

        Assert.Empty(await reader.ObservedAsync("7", ["lodash"]));
    }
}
