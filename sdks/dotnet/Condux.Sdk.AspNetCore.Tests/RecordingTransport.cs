using System.Net;

namespace Condux.Sdk.AspNetCore.Tests;

// Records the request body the SDK sends, and responds 202 — so a test can assert what reached the relay
// (and that nothing was sent at all) with no real network.
internal sealed class RecordingTransport : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }
}
