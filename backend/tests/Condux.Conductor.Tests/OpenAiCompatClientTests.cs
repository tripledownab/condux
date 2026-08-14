using System.Net;
using System.Text.Json;
using Condux.Agent;
using Xunit;

namespace Condux.Conductor.Tests;

public class OpenAiCompatClientTests
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

    [Fact]
    public async Task Posts_chat_completions_with_bearer_auth_and_returns_the_message()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"role":"assistant","content":"{\"summary\":\"x\"}"}}],
             "usage":{"prompt_tokens":900,"completion_tokens":120}}
            """);
        var client = new OpenAiCompatClient(new HttpClient(handler));

        var output = await client.CreateAsync(
            "gpt-x", "system prompt", "user prompt", "sk-openai-test", "https://vllm.test/v1");

        Assert.Equal("""{"summary":"x"}""", output.Text);
        Assert.Equal(900, output.InputTokens);
        Assert.Equal(120, output.OutputTokens);
        Assert.Equal("openai-compat", client.Provider);
        Assert.Equal("https://vllm.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(
            "sk-openai-test", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization.Scheme);

        using var body = JsonDocument.Parse(handler.LastBody);
        Assert.Equal("gpt-x", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Throws_on_a_non_success_response()
    {
        var client = new OpenAiCompatClient(
            new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "nope")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateAsync("m", "s", "u", "k", "https://x/v1"));
    }
}
