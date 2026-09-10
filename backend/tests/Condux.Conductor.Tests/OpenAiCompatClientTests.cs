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

    /// <summary>
    /// The org supplies this base URL and the conductor runs inside the deployment, so an unchecked one
    /// is a request we make from the inside on the org's behalf. It is validated where it is stored too,
    /// but a row written before that check existed was never validated.
    ///
    /// The assertion that carries the weight is the second: no request was sent. A test that only checked
    /// for a throw would pass against a client that sent the request and then failed on the response.
    ///
    /// Measured by removing the guard: only <c>file://</c> actually depends on it. The other three are
    /// malformed enough that the request pipeline rejects them anyway, so they document the rule without
    /// protecting it. Keep the <c>file://</c> case whatever else changes here, since a well-formed URL
    /// with a scheme we do not speak is the one an attacker would choose.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/v1")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Refuses_a_base_url_that_is_not_absolute_http_without_sending_anything(string baseUrl)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var client = new OpenAiCompatClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateAsync("gpt-x", "system", "user", "sk-test", baseUrl));

        Assert.Null(handler.LastRequest);
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
