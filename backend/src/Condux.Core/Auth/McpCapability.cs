namespace Condux.Core.Auth;

/// <summary>
/// What an MCP token is allowed to do (ADR-0046). Values are ordered so a
/// <c>capability &gt;= required</c> comparison gates a tool, exactly as <see cref="OrgRole"/> gates an
/// endpoint. Persisted as the numeric value in <c>mcp_tokens.capability</c>.
///
/// Ordered rather than flags: flags would express "fix but not triage", which is not a combination
/// anyone wants, and slice 2's fix capability slots in above Triage.
/// </summary>
public enum McpCapability
{
    /// <summary>Read the project's issues and events. What every token issued before ADR-0046 has.</summary>
    Read = 0,

    /// <summary>Read, plus set an issue's status and leave a note on it.</summary>
    Triage = 1,
}

/// <summary>Wire (de)serialization for <see cref="McpCapability"/>, stable lower-case names for the API.</summary>
public static class McpCapabilities
{
    /// <summary>The lower-case wire name used in API payloads.</summary>
    public static string Name(McpCapability capability) => capability switch
    {
        McpCapability.Triage => "triage",
        _ => "read",
    };

    /// <summary>
    /// Parses a wire capability name (case-insensitive); false if it isn't a known one.
    /// <paramref name="capability"/> is set to <see cref="McpCapability.Read"/> when parsing fails, so a
    /// caller that ignores the result mints the least authority rather than the most.
    /// </summary>
    public static bool TryParse(string? value, out McpCapability capability)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "triage": capability = McpCapability.Triage; return true;
            case "read": capability = McpCapability.Read; return true;
            default: capability = McpCapability.Read; return false;
        }
    }
}
