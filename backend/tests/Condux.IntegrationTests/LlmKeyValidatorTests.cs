using System.Net;
using Condux.ControlPlane.Llm;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// A stub-HTTP unit test (no Docker, so it runs in the CI unit filter) for the BYO-key model lister:
/// Anthropic uses <c>x-api-key</c> against its fixed endpoint; an OpenAI-compatible provider uses a
/// Bearer key against <c>{baseUrl}/models</c>. A non-2xx yields null (invalid), and an unsupported or
/// under-specified request yields null without a call.
/// </summary>
public class LlmKeyValidatorTests
{
    private const string AnthropicModelsBody = """
        {"data":[
          {"id":"claude-opus-4-8","display_name":"Claude Opus 4.8","type":"model"},
          {"id":"claude-haiku-4-5","display_name":"Claude Haiku 4.5","type":"model"}
        ],"has_more":false}
        """;

    private const string OpenAiModelsBody = """
        {"object":"list","data":[{"id":"gpt-x","object":"model"},{"id":"llama-3","object":"model"}]}
        """;

    private sealed class StubHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Anthropic_200_lists_the_models_with_the_api_key_header()
    {
        var handler = new StubHandler(HttpStatusCode.OK, AnthropicModelsBody);
        var validator = new LlmKeyValidator(new HttpClient(handler));

        var models = await validator.ListModelsAsync("anthropic", "", "sk-ant-good");

        Assert.NotNull(models);
        Assert.Collection(models!,
            m => Assert.Equal("claude-opus-4-8", m.Id),
            m => Assert.Equal("Claude Haiku 4.5", m.DisplayName));
        Assert.Equal("/v1/models", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("sk-ant-good", handler.LastRequest.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task OpenAiCompatible_lists_models_from_the_base_url_with_a_bearer_key()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OpenAiModelsBody);
        var validator = new LlmKeyValidator(new HttpClient(handler));

        var models = await validator.ListModelsAsync("openai-compat", "https://vllm.test/v1", "sk-x");

        Assert.NotNull(models);
        // OpenAI models carry no display_name, so the id is used for both.
        Assert.Collection(models!,
            m => Assert.Equal("gpt-x", m.DisplayName),
            m => Assert.Equal("llama-3", m.Id));
        Assert.Equal("https://vllm.test/v1/models", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-x", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task OpenAiCompatible_without_a_base_url_is_rejected_without_a_call()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OpenAiModelsBody);
        var validator = new LlmKeyValidator(new HttpClient(handler));

        Assert.Null(await validator.ListModelsAsync("openai-compat", "", "sk-x"));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task A_non_2xx_response_means_the_key_is_invalid()
    {
        var validator = new LlmKeyValidator(new HttpClient(new StubHandler(HttpStatusCode.Unauthorized)));
        Assert.Null(await validator.ListModelsAsync("anthropic", "", "sk-ant-bad"));
    }

    [Fact]
    public async Task An_unsupported_provider_is_rejected_without_a_call()
    {
        var handler = new StubHandler(HttpStatusCode.OK, AnthropicModelsBody);
        var validator = new LlmKeyValidator(new HttpClient(handler));

        Assert.Null(await validator.ListModelsAsync("bedrock", "https://x", "key"));
        Assert.Null(handler.LastRequest);
    }
}
