using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Condux.Core.FixEngine;

namespace Condux.Agent;

/// <summary>
/// The OpenAI side of an agentic run: maps the neutral tool schema and results to Chat Completions'
/// function-calling shape and back, holding the conversation so <see cref="AgentLoop"/> stays stateless.
/// One instance per run.
///
/// This one adapter reaches most of the ecosystem, since Azure OpenAI, vLLM, Ollama, LiteLLM, DeepSeek,
/// Kimi, Qwen and Gemini's compatibility endpoint all speak this format with only a base URL between
/// them. Adding a provider is a base URL, not code.
/// </summary>
internal sealed class OpenAiToolConversation(
    HttpClient http, string apiKey, string model, string baseUrl, string systemPrompt, string firstMessage)
    : IAgentConversation
{
    private const int MaxTokensPerTurn = 8192;

    private readonly JsonArray messages = [];

    /// <summary>The tools offered, from the workspace's capabilities.</summary>
    public IReadOnlyList<AgentToolSchema> AvailableTools { get; init; } = AgentToolCatalog.All;

    public async Task<AgentTurn> NextAsync(
        IReadOnlyList<AgentToolResult> results, CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            // The system prompt is a message here rather than a top-level field, unlike Anthropic's.
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = firstMessage });
        }
        else
        {
            foreach (var result in results)
            {
                // One message per result, correlated by id — where Anthropic nests every result into a
                // single user turn. Sending them Anthropic-style is silently accepted and then ignored.
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.Id,
                    ["content"] = result.Content,
                });
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokensPerTurn,
            ["messages"] = messages.DeepClone(),
            ["tools"] = Tools(),
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible API returned {(int)response.StatusCode}: {ModelApiError.Describe(payload)}");
        }

        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("OpenAI-compatible API returned an unreadable response.");
        var message = root["choices"]?.AsArray().FirstOrDefault()?["message"]?.AsObject()
            ?? throw new InvalidOperationException("OpenAI-compatible API returned no message.");

        // Echo the assistant turn back verbatim next time: the tool_call ids have to match the tool
        // messages we send, or the model cannot correlate them.
        messages.Add(message.DeepClone());

        var usage = root["usage"]?.AsObject();
        var inputTokens = usage?["prompt_tokens"]?.GetValue<long>() ?? 0;
        var outputTokens = usage?["completion_tokens"]?.GetValue<long>() ?? 0;

        var calls = new List<AgentToolCall>();
        foreach (var call in message["tool_calls"]?.AsArray() ?? [])
        {
            var function = call?["function"];
            calls.Add(new AgentToolCall(
                call?["id"]?.GetValue<string>() ?? string.Empty,
                function?["name"]?.GetValue<string>() ?? string.Empty,
                Arguments(function?["arguments"])));
        }

        return calls.Count > 0
            ? AgentTurn.Calls(calls, inputTokens, outputTokens)
            : AgentTurn.Answer(message["content"]?.GetValue<string>() ?? "", inputTokens, outputTokens);
    }

    /// <summary>
    /// Arguments arrive as a JSON *string* to be parsed, not as an object — the one real difference from
    /// Anthropic's shape, and the one that silently yields empty arguments if you read it as an object.
    /// A model can also emit malformed JSON here, which is its mistake to recover from rather than a fault,
    /// so it degrades to no arguments and the loop reports the missing one back.
    /// </summary>
    private static Dictionary<string, string> Arguments(JsonNode? raw)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw?.GetValue<string>() is not { Length: > 0 } json)
        {
            return arguments;
        }

        JsonObject? parsed;
        try
        {
            parsed = JsonNode.Parse(json)?.AsObject();
        }
        catch (JsonException)
        {
            return arguments;
        }

        foreach (var (key, value) in parsed ?? [])
        {
            arguments[key] = value is JsonValue text && text.TryGetValue<string>(out var s)
                ? s
                : value?.ToJsonString() ?? string.Empty;
        }

        return arguments;
    }

    private JsonArray Tools()
    {
        var tools = new JsonArray();
        foreach (var tool in AvailableTools)
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

            // Wrapped in a "function" object, where Anthropic takes the schema flat.
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                },
            });
        }

        return tools;
    }

}
