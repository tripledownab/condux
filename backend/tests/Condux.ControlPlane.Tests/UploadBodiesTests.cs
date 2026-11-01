using Condux.ControlPlane.Uploads;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Condux.ControlPlane.Tests;

/// <summary>
/// The upload reader's hold on the request, which the test client the integration tests use cannot show:
/// Kestrel's minimum data rate is set on an HTTP/1.x request and left alone on an HTTP/2 or HTTP/3 stream,
/// which refuses one; Kestrel's request size limit is raised to the route's cap; and a body Kestrel itself
/// refuses is answered with Kestrel's status rather than raised as an error of ours.
/// </summary>
public class UploadBodiesTests
{
    [Theory]
    [InlineData("HTTP/1.1")]
    [InlineData("HTTP/1.0")]
    public async Task An_http1_upload_is_held_to_the_minimum_rate(string protocol)
    {
        var rate = new RecordingRate();
        var http = Upload(protocol, new MemoryStream([1, 2, 3]), rate);

        var read = await UploadBodies.ReadAsync(http, 100);

        Assert.False(read.IsRefused);
        Assert.Same(UploadBodies.MinRate, rate.MinDataRate);
        read.Body.Dispose();
    }

    [Theory]
    [InlineData("HTTP/2")]
    [InlineData("HTTP/3")]
    public async Task A_later_protocols_upload_is_read_without_a_rate_kestrel_would_refuse(string protocol)
    {
        var http = Upload(protocol, new MemoryStream([1, 2, 3]), new RefusingRate());

        var read = await UploadBodies.ReadAsync(http, 100);

        Assert.False(read.IsRefused);
        read.Body.Dispose();
    }

    [Fact]
    public async Task Kestrels_request_size_limit_is_set_to_the_routes_cap()
    {
        var size = new RecordingSize();
        var http = Upload("HTTP/1.1", new MemoryStream([1, 2, 3]), new RecordingRate());
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(size);

        var read = await UploadBodies.ReadAsync(http, 64L * 1024 * 1024);

        Assert.Equal(64L * 1024 * 1024, size.MaxRequestBodySize);
        read.Body!.Dispose();
    }

    [Theory]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status400BadRequest)]
    public async Task A_body_kestrel_refuses_is_answered_with_kestrels_status(int status)
    {
        var http = Upload("HTTP/1.1", new RefusedBody(status), new RecordingRate());

        var read = await UploadBodies.ReadAsync(http, 100);

        Assert.True(read.IsRefused);
        Assert.Equal(status, read.Refusal.StatusCode);
    }

    private static DefaultHttpContext Upload(string protocol, Stream body, IHttpMinRequestBodyDataRateFeature rate)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<UploadBodyBudget>().BuildServiceProvider(),
        };
        http.Request.Protocol = protocol;
        http.Request.Body = body;
        http.Features.Set(rate);
        return http;
    }

    private sealed class RecordingRate : IHttpMinRequestBodyDataRateFeature
    {
        public MinDataRate? MinDataRate { get; set; }
    }

    // What an HTTP/2 or HTTP/3 stream in Kestrel does with a rate (Http2Stream.FeatureCollection and
    // Http3Stream.FeatureCollection in aspnetcore): it takes null, and refuses anything else.
    private sealed class RefusingRate : IHttpMinRequestBodyDataRateFeature
    {
        public MinDataRate? MinDataRate
        {
            get => throw new NotSupportedException();
            set
            {
                if (value is not null)
                {
                    throw new NotSupportedException();
                }
            }
        }
    }

    private sealed class RecordingSize : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    // A body Kestrel gives up on: too slow (408), too large (413) or cut short (400).
    private sealed class RefusedBody(int status) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("refused by Kestrel", status);
    }
}
