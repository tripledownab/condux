using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>
/// The relay on real Kestrel, for what TestServer cannot show: its request size limit, which refuses a body
/// past CONDUX_MAX_INGEST_BYTES by throwing BadHttpRequestException(413) inside the read (from Kestrel for a
/// plain body, from the decompression middleware for a compressed one), and its own refusals of a malformed
/// body. A body past the cap is the relay's own refusal, answered in each endpoint's format; any other
/// refusal the server makes is the sender's, so the self-report wrapper does not file it (ADR-0044), while a
/// fault of the relay's own still is.
/// </summary>
public sealed class RelayOnKestrelTests
{
    private const int Cap = 100_000;

    // Each way the host meets a body past its limit: a compressed one through the decompression middleware,
    // a declared Content-Length on the first read, and a chunked one as its bytes are counted.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_sentry_body_past_the_cap_is_the_relays_own_413_and_not_self_reported(bool gzipped, bool chunked)
    {
        var reports = new RecordingTransport();
        using var factory = Relay(reports);
        using var client = factory.CreateClient();
        var req = Post("/api/1/store/", new byte[5 * Cap], gzipped, "application/json");
        req.Headers.TransferEncodingChunked = chunked;

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Contains("payload_too_large", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, reports.Count);
    }

    /// <summary>
    /// OTLP requires every 4xx a google.rpc.Status in the request's encoding. Past the cap the host's own 413
    /// used to answer first, with no body at all. 8 is RESOURCE_EXHAUSTED.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_otlp_export_past_the_cap_is_answered_with_a_status(bool gzipped)
    {
        using var factory = Relay(new RecordingTransport());
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(Post("/api/1/v1/logs", new byte[5 * Cap], gzipped, "application/x-protobuf"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Equal("application/x-protobuf", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal([0x08, 0x08], body[..2]);
    }

    /// <summary>
    /// A chunked body whose chunk size is not hexadecimal: Kestrel refuses it inside the read with
    /// BadHttpRequestException(400), which passes through the self-report wrapper as the sender's error.
    /// </summary>
    [Fact]
    public async Task A_body_the_server_refuses_as_malformed_is_not_self_reported()
    {
        var reports = new RecordingTransport();
        using var factory = Relay(reports);
        using var client = factory.CreateClient();
        var server = client.BaseAddress!;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /api/1/store/ HTTP/1.1\r\nHost: localhost\r\nx-condux-auth: devkey\r\n"
            + "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n"));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();

        Assert.StartsWith("HTTP/1.1 400", statusLine, StringComparison.Ordinal);
        Assert.Equal(0, reports.Count);
    }

    [Fact]
    public async Task A_fault_of_the_relays_own_is_still_self_reported()
    {
        var reports = new RecordingTransport();
        using var factory = Relay(reports, new FailingPublisher());
        using var client = factory.CreateClient();

        var resp = await client.SendAsync(
            Post("/api/1/store/", """{"message":"boom","level":"error"}"""u8.ToArray(), gzipped: false, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(1, reports.Count);
    }

    private static WebApplicationFactory<Program> Relay(RecordingTransport reports, IEventPublisher? publisher = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_MAX_INGEST_BYTES", Cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(new ConduxClient(new ConduxOptions
                {
                    Dsn = "https://key@self.condux.test/1",
                    Transport = reports,
                    MaxRetries = 0,
                }));
                if (publisher is not null)
                {
                    s.AddSingleton(publisher);
                }
            });
        });
        factory.UseKestrel(0);
        factory.StartServer();
        return factory;
    }

    private static HttpRequestMessage Post(string path, byte[] body, bool gzipped, string contentType)
    {
        var content = new ByteArrayContent(gzipped ? Gzip(body) : body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (gzipped)
        {
            content.Headers.ContentEncoding.Add("gzip");
        }
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        req.Headers.Add("x-condux-auth", "devkey");
        return req;
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
        {
            gzip.Write(payload);
        }
        return buffer.ToArray();
    }

    /// <summary>Counts what self-reporting would send, and answers it as the relay would. A report starts
    /// before the wrapper rethrows, so one would have reached it by the time the response is written.</summary>
    private sealed class RecordingTransport : HttpMessageHandler
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>A broker that is down: a fault of the relay's own, nothing the sender did.</summary>
    private sealed class FailingPublisher : IEventPublisher
    {
        public Task PublishAsync(
            string projectId, Event e, int retentionDays, long sentAtUnixMs, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("broker unavailable");
    }
}
