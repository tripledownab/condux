using System.Net;
using System.Text.Json;
using Xunit;

namespace Condux.Conductor.Tests;

public class AnthropicMessagesClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static AnthropicMessagesClient Create(StubHandler handler) =>
        new(new HttpClient(handler), new AnthropicOptions("sk-ant-test") { BaseUrl = "https://anthropic.test" });

    [Fact]
    public async Task Sends_the_expected_request_and_returns_only_text_blocks()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"content":[{"type":"thinking","thinking":"..."},{"type":"text","text":"{\"a\":1}"}],
             "stop_reason":"end_turn","usage":{"input_tokens":1200,"output_tokens":345}}
            """);
        var client = Create(handler);

        var output = await client.CreateAsync("claude-opus-4-8", "system prompt", "user prompt", "sk-ant-test", "");

        Assert.Equal("""{"a":1}""", output.Text); // thinking blocks are skipped
        Assert.Equal(1200, output.InputTokens); // usage feeds the cost metering
        Assert.Equal(345, output.OutputTokens);
        Assert.Equal("https://anthropic.test/v1/messages", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", handler.LastRequest.Headers.GetValues("x-api-key").Single()); // per-call key
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());

        using var body = JsonDocument.Parse(handler.LastBody);
        Assert.Equal("claude-opus-4-8", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("adaptive", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("system prompt", body.RootElement.GetProperty("system").GetString());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Surfaces_api_errors_and_empty_output_as_failures()
    {
        var error = new StubHandler(
            HttpStatusCode.Unauthorized, """{"error":{"type":"authentication_error"}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(error).CreateAsync("m", "s", "u", "k", ""));

        var empty = new StubHandler(HttpStatusCode.OK, """{"content":[],"stop_reason":"refusal"}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create(empty).CreateAsync("m", "s", "u", "k", ""));
        Assert.Contains("refusal", ex.Message);
    }
}
