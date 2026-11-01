using System.Net;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>What <see cref="HttpResponses.EnsureSuccessAsync"/> reports, and that it leaves a successful
/// body unread. Needs no container, so it carries no Integration category and runs in the unit pass.</summary>
public class HttpResponsesTest
{
    [Fact]
    public async Task A_failure_carries_its_status_request_and_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("System.NullReferenceException: boom\n   at Signup()"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/auth/signup?x=1"),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(response.EnsureSuccessAsync);

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("500", ex.Message);
        Assert.Contains("POST /api/auth/signup?x=1", ex.Message);
        Assert.Contains("System.NullReferenceException: boom", ex.Message);
    }

    [Fact]
    public async Task A_success_that_is_not_the_expected_status_carries_its_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("queued instead"),
        };

        await response.EnsureStatusAsync(HttpStatusCode.Accepted);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => response.EnsureStatusAsync(HttpStatusCode.OK));

        Assert.Contains("expected 200, got 202", ex.Message);
        Assert.Contains("queued instead", ex.Message);
    }

    [Fact]
    public async Task A_long_body_is_cut_short()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(new string('x', 20_000)),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(response.EnsureSuccessAsync);

        Assert.InRange(ex.Message.Length, 8_000, 8_200);
    }

    [Fact]
    public async Task A_success_leaves_its_body_unread()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnreadableStream()),
        };

        await response.EnsureSuccessAsync();
    }

    // A stream body that fails any read, standing in for a live event stream that never ends.
    private sealed class UnreadableStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("the body was read");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            throw new InvalidOperationException("the body was read");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new InvalidOperationException("the body was read");
    }
}
