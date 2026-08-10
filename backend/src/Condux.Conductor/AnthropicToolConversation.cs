using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Condux.Core.FixEngine;

namespace Condux.Conductor;

/// <summary>
/// Anthropic's side of an agentic run: maps the neutral tool schema and results to the Messages API's
/// tool-use shape and back, holding the conversation so <see cref="AgentLoop"/> stays stateless. One
/// instance per run.
///
/// Extended thinking is deliberately off here. It requires preserving thinking blocks verbatim across
/// turns, which buys nothing for a loop whose reasoning is already externalised as tool calls.
/// </summary>
internal sealed class AnthropicToolConversation(
    HttpClient http, string apiKey, string model, string systemPrompt, string firstMessage) : IAgentConversation
{
    private const int MaxTokensPerTurn = 8192;

    private readonly JsonArray messages = [];

    public string BaseUrl { get; init; } = "https://api.anthropic.com";

    public async Task<AgentTurn> NextAsync(
        IReadOnlyList<AgentToolResult> results, CancellationToken cancellationToken = default)
    {
        messages.Add(messages.Count == 0 ? UserText(firstMessage) : ToolResults(results));

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokensPerTurn,
            ["system"] = systemPrompt,
            ["tools"] = Tools(),
            ["messages"] = messages.DeepClone(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Anthropic returned {(int)response.StatusCode}: {Truncate(payload, 500)}");
        }

        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("Anthropic returned an unreadable response.");
        var content = root["content"]?.AsArray()
            ?? throw new InvalidOperationException("Anthropic returned no content.");

        // Echo the assistant turn back verbatim on the next request: the tool_use ids must match the
        // tool_result ids we send, so the model can correlate them.
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

        var usage = root["usage"]?.AsObject();
        var inputTokens = usage?["input_tokens"]?.GetValue<long>() ?? 0;
        var outputTokens = usage?["output_tokens"]?.GetValue<long>() ?? 0;

        var calls = new List<AgentToolCall>();
        var text = new List<string>();
        foreach (var block in content)
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "tool_use":
                    calls.Add(new AgentToolCall(
                        block["id"]?.GetValue<string>() ?? string.Empty,
                        block["name"]?.GetValue<string>() ?? string.Empty,
                        Arguments(block["input"])));
                    break;
                case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } value:
                    text.Add(value);
                    break;
            }
        }

        return calls.Count > 0
            ? AgentTurn.Calls(calls, inputTokens, outputTokens)
            : AgentTurn.Answer(string.Join("\n\n", text), inputTokens, outputTokens);
    }

    /// <summary>Tool arguments arrive as arbitrary JSON. Flatten to strings, which is what the neutral
    /// tool contract takes, serialising a non-string value rather than dropping it.</summary>
    private static Dictionary<string, string> Arguments(JsonNode? input)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input is JsonObject json)
        {
            foreach (var (key, value) in json)
            {
                arguments[key] = value is JsonValue text && text.TryGetValue<string>(out var raw)
                    ? raw
                    : value?.ToJsonString() ?? string.Empty;
            }
        }

        return arguments;
    }

    private static JsonObject UserText(string text) => new()
    {
        ["role"] = "user",
        ["content"] = text,
    };

    private static JsonObject ToolResults(IReadOnlyList<AgentToolResult> results)
    {
        var blocks = new JsonArray();
        foreach (var result in results)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = result.Id,
                ["content"] = result.Content,
                ["is_error"] = result.IsError,
            });
        }

        return new JsonObject { ["role"] = "user", ["content"] = blocks };
    }

    private static JsonArray Tools()
    {
        var tools = new JsonArray();
        foreach (var tool in AgentToolCatalog.All)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var parameter in tool.Parameters)
            {
                properties[parameter.Name] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = parameter.Description,
                };
                if (parameter.Required)
                {
                    required.Add(parameter.Name);
                }
            }

            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                },
            });
        }

        return tools;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
