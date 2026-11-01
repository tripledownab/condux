namespace Condux.Relay.Ingest;

/// <summary>The span attributes an event made from a span keeps as tags. Every other attribute is dropped.
/// A failed span of any application becomes an event, and a span can carry anything: the prompt and the
/// tool arguments of an agent, the text of a query, the URL of a request, who the user was. Instrumentations
/// send prompts by default, and one turns capture on for any setting value it does not recognise, so a list
/// of what to drop would be one new spelling away from storing content. The list is what is known to
/// describe the operation and not its data.</summary>
internal static class OtlpSpanTags
{
    private static readonly HashSet<string> Kept = new(StringComparer.Ordinal)
    {
        "gen_ai.operation.name",
        "gen_ai.provider.name",
        "gen_ai.system",
        "gen_ai.request.model",
        "gen_ai.response.model",
        "gen_ai.agent.name",
        "gen_ai.agent.id",
        "gen_ai.tool.name",
        "gen_ai.tool.type",
        "error.type",
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "rpc.system",
        "rpc.service",
        "rpc.method",
        "mcp.method.name",
        "mcp.protocol.version",
        "traceloop.span.kind",
    };

    public static bool IsKept(string key) => Kept.Contains(key);
}
