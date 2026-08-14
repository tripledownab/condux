using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Condux.Agent;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The OpenAI mapping of the neutral tool contract. The loop is already covered; what matters here is
/// the wire shape, because it differs from Anthropic's in three ways that each fail silently rather than
/// loudly: arguments arrive as a JSON string, tool results are one message each rather than nested, and
/// the system prompt is a message rather than a field.
/// </summary>
public class OpenAiToolConversationTests
{
    private sealed class StubHandler(params string[] replies) : HttpMessageHandler
    {
        private int index;

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var body = replies[Math.Min(index, replies.Length - 1)];
            index++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private static OpenAiToolConversation Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), "sk-test", "gpt-test", "https://llm.test/v1", "system", "first");

    private static string ToolCall(string id, string name, object arguments) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id,
                                type = "function",
                                // A JSON *string*, which is the shape OpenAI actually sends.
                                function = new { name, arguments = JsonSerializer.Serialize(arguments) },
                            },
                        },
                    },
                },
            },
            usage = new { prompt_tokens = 100, completion_tokens = 20 },
        });

    [Fact]
    public async Task Reads_a_tool_call_whose_arguments_are_a_json_string()
    {
        // Read as an object it yields nothing, the loop reports a missing argument, and the agent burns
        // its budget retrying a call it made correctly.
        var handler = new StubHandler(
            ToolCall("call_1", AgentToolNames.ReadFile, new { path = "src/cart.ts" }));

        var turn = await Create(handler).NextAsync([]);

        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal(AgentToolNames.ReadFile, call.Name);
        Assert.Equal("src/cart.ts", call.Argument("path"));
        Assert.Equal(100, turn.InputTokens);
        Assert.Equal(20, turn.OutputTokens);
    }

    [Fact]
    public async Task Offers_the_tools_wrapped_the_way_this_api_expects()
    {
        var handler = new StubHandler(ToolCall("call_1", AgentToolNames.Finish, new { summary = "done" }));

        await Create(handler).NextAsync([]);

        var body = JsonNode.Parse(handler.Requests[0])!.AsObject();
        var tool = body["tools"]!.AsArray()[0]!;
        // Nested under "function", where Anthropic takes the schema flat.
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal(AgentToolNames.ListFiles, tool["function"]!["name"]!.GetValue<string>());
        // The system prompt is a message here, not a top-level field.
        Assert.Equal("system", body["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sends_each_tool_result_as_its_own_message_keyed_to_its_call()
    {
        var handler = new StubHandler(
            ToolCall("call_1", AgentToolNames.ReadFile, new { path = "a.ts" }),
            ToolCall("call_2", AgentToolNames.Finish, new { summary = "done" }));
        var conversation = Create(handler);

        await conversation.NextAsync([]);
        await conversation.NextAsync([new AgentToolResult("call_1", "file contents")]);

        var messages = JsonNode.Parse(handler.Requests[1])!.AsObject()["messages"]!.AsArray();
        var toolMessage = messages.Last(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Equal("call_1", toolMessage["tool_call_id"]!.GetValue<string>());
        Assert.Equal("file contents", toolMessage["content"]!.GetValue<string>());
        // The assistant turn is echoed back, or the ids have nothing to correlate against.
        Assert.Contains(messages, m => m!["role"]!.GetValue<string>() == "assistant");
    }

    [Fact]
    public async Task Plain_text_with_no_tool_call_is_an_answer()
    {
        var handler = new StubHandler(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "I am done." } } },
            usage = new { prompt_tokens = 5, completion_tokens = 2 },
        }));

        var turn = await Create(handler).NextAsync([]);

        Assert.Empty(turn.ToolCalls);
        Assert.Equal("I am done.", turn.FinalText);
    }

    [Fact]
    public async Task Malformed_arguments_degrade_to_none_rather_than_killing_the_run()
    {
        // Models do emit invalid JSON here. That is the model's mistake to recover from, so the loop
        // reports the missing argument back and the agent retries, rather than the run dying.
        var handler = new StubHandler(JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call_1",
                                type = "function",
                                function = new { name = AgentToolNames.ReadFile, arguments = "{not json" },
                            },
                        },
                    },
                },
            },
        }));

        var turn = await Create(handler).NextAsync([]);

        Assert.Equal("", Assert.Single(turn.ToolCalls).Argument("path"));
    }

    [Fact]
    public async Task A_failed_call_says_what_the_endpoint_returned()
    {
        var handler = new FailingHandler();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(handler).NextAsync([]));

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"bad key"}}"""),
            });
    }
}
