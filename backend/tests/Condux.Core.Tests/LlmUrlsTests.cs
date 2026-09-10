using Condux.Core.Http;
using Condux.Core.Llm;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The model-provider base URL policy over the shared shape check (see <c>HttpUrlsTests</c>). The one
/// difference is deliberate: this value may be absent, an SSO endpoint may not.
/// </summary>
public class LlmUrlsTests
{
    /// <summary>
    /// The model-provider policy: absent is fine, because Anthropic's endpoint is fixed and the value is
    /// ignored. Whether a provider REQUIRES one is a separate question the caller answers, so this must
    /// not start refusing an empty value or it becomes a second copy of the provider table.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidBaseUrl_accepts_an_absent_base_url(string? url) =>
        Assert.True(LlmUrls.IsValidBaseUrl(url));

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://vllm.internal:8000/v1")]
    public void IsValidBaseUrl_accepts_a_well_formed_url(string url) =>
        Assert.True(LlmUrls.IsValidBaseUrl(url));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/v1")]
    [InlineData("not a url")]
    public void IsValidBaseUrl_refuses_a_present_but_malformed_url(string url) =>
        Assert.False(LlmUrls.IsValidBaseUrl(url));

    /// <summary>
    /// The two policies agree about everything except absence. Pins the relationship: if someone
    /// re-inlines either one, this is what notices they have stopped sharing a definition.
    /// </summary>
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example")]
    [InlineData("not a url")]
    public void The_two_policies_differ_only_about_an_absent_value(string url)
    {
        Assert.Equal(HttpUrls.IsAbsoluteHttp(url), LlmUrls.IsValidBaseUrl(url));

        Assert.False(HttpUrls.IsAbsoluteHttp(""));
        Assert.True(LlmUrls.IsValidBaseUrl(""));
    }
}
