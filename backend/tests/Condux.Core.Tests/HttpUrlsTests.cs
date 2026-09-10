using Condux.Core.Http;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The shape check for a URL an admin supplies and this platform then fetches. Shared by the enterprise
/// SSO endpoints and the model provider's base URL, which differ only in whether the value may be absent.
/// </summary>
public class HttpUrlsTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("https://models.example.com")]
    public void IsAbsoluteHttp_accepts_an_absolute_http_or_https_url(string url) =>
        Assert.True(HttpUrls.IsAbsoluteHttp(url));

    /// <summary>
    /// A scheme we do not speak is refused rather than handed to HttpClient. <c>file://</c> is the one
    /// that matters: it turns an outbound call into a local read.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    [InlineData("ftp://example.com")]
    public void IsAbsoluteHttp_refuses_a_scheme_we_do_not_speak(string url) =>
        Assert.False(HttpUrls.IsAbsoluteHttp(url));

    /// <summary>
    /// Anything that is not an absolute URL. <c>//evil.example</c> is the interesting one: it is
    /// scheme-relative, so a naive concatenation would resolve it against whatever host we are on.
    /// </summary>
    [Theory]
    [InlineData("//evil.example/v1")]
    [InlineData("/v1/models")]
    [InlineData("api.openai.com")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAbsoluteHttp_refuses_anything_that_is_not_an_absolute_url(string? url) =>
        Assert.False(HttpUrls.IsAbsoluteHttp(url));
}
