using Condux.ControlPlane.Endpoints;
using Condux.ControlPlane.Realtime;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Condux.ControlPlane.Tests;

/// <summary>The live stream's handler when the host is already stopping as the request arrives, which is when
/// every open dashboard reconnects during a deploy.</summary>
public class ProjectEventStreamTests
{
    [Fact]
    public async Task A_stream_opened_after_stopping_began_closes_without_a_fault()
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        // Any exception here would reach the request pipeline, which reports it as a server fault.
        // Bounded, so a handler that ignored the token fails here instead of streaming heartbeats for ever.
        await ProjectEventStreamEndpoints.StreamAsync(http, new ProjectEventHub(), 1, stopping.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, body.Length);
    }
}
