using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// What an MCP token may do (ADR-0046). The ordering and the parse default are the two properties the
/// endpoint leans on: a tool is gated by <c>capability &gt;= required</c>, and anything unrecognised has
/// to land on the least authority rather than the most.
/// </summary>
public sealed class McpCapabilitiesTests
{
    [Fact]
    public void Read_IsTheLowestAuthority_SoTheGateIsAnOrdering()
    {
        Assert.True(McpCapability.Triage >= McpCapability.Read);
        Assert.False(McpCapability.Read >= McpCapability.Triage);
        // Read is zero because it is the column default, which is what keeps the migration from widening
        // a token that already exists.
        Assert.Equal(0, (int)McpCapability.Read);
    }

    [Theory]
    [InlineData("read", McpCapability.Read)]
    [InlineData("triage", McpCapability.Triage)]
    [InlineData("TRIAGE", McpCapability.Triage)]
    [InlineData("  triage  ", McpCapability.Triage)]
    public void TryParse_AcceptsTheWireNames(string value, McpCapability expected)
    {
        Assert.True(McpCapabilities.TryParse(value, out var capability));
        Assert.Equal(expected, capability);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("write")]
    [InlineData("fix")]
    public void TryParse_RejectsAnythingElse_AndFallsBackToRead(string? value)
    {
        Assert.False(McpCapabilities.TryParse(value, out var capability));
        // A caller that ignores the false gets the least authority, never the most.
        Assert.Equal(McpCapability.Read, capability);
    }

    [Fact]
    public void Name_RoundTripsEveryValue()
    {
        // Every enum member, so adding one without a wire name fails here rather than serializing as
        // "read" and silently reporting the wrong authority to the dashboard.
        foreach (var capability in Enum.GetValues<McpCapability>())
        {
            Assert.True(McpCapabilities.TryParse(McpCapabilities.Name(capability), out var parsed));
            Assert.Equal(capability, parsed);
        }
    }
}
