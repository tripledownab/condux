using System.Net;
using System.Text;
using System.Threading.Channels;
using Condux.Core.Http;
using Condux.Core.Messaging;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Relay.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>
/// What an unfinished body may hold: its sender's share of the relay's body budget
/// and no more, and that only until it stops arriving for <see cref="StalledBodyWatch.Limit"/>. Driven
/// through the real endpoint with a body that sends its first bytes and then waits, and a fake clock.
/// </summary>
public class IngestBodyHoldTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Event = """{"message":"boom","level":"error"}""";

    // AllowAutoRedirect off: the factory's redirect handler buffers the whole request body so it can send
    // it again, and the server would see nothing until the body ended.
    private HttpClient Client(IngestBodyBudget budget, FakeTimeProvider clock, IQuotaMeter? quota = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_TRUSTED_PROXIES", "10.0.0.0/8");
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IEventPublisher>(new InMemoryEventPublisher());
                s.AddSingleton<IProjectStore>(new TwoOrgProjectStore());
                s.AddSingleton(budget);
                s.AddSingleton<TimeProvider>(clock);
                if (quota is not null) s.AddSingleton(quota);
                s.AddSingleton<IStartupFilter>(new BehindAProxy());
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // Projects 11 and 12 belong to one organisation and 13 to another (TwoOrgProjectStore). The share is
    // smaller than one unfinished body plus one more read, so the first holds its sender's share: the same
    // organisation from the same address is refused, and a different organisation or a different address
    // still reads. The last is the point of the address: a DSN is public, so whoever holds one can reach
    // that organisation's ceiling from their own address, never its users' shares within it.
    [Fact]
    public async Task An_unfinished_body_holds_only_its_senders_share()
    {
        var budget = new IngestBodyBudget(1024 * 1024, 16 * 1024, 8 * 1024);
        var client = Client(budget, new FakeTimeProvider());
        var held = new UnfinishedBody(1000);

        var holding = client.SendAsync(Post("11", held, From));
        await held.Sent.Task;

        // The first body is read on the server's schedule, so the test waits until it holds the share.
        await WaitUntilAsync(() => Task.FromResult(IsCharged(budget, 1024 * 1024, RequestBodies.FirstPieceBytes + 1000)));
        var sameSender = await client.SendAsync(Post("12", new StringContent(Event), From));
        var sameOrganisationElsewhere = await client.SendAsync(Post("12", new StringContent(Event), "198.51.100.2"));
        var otherOrganisation = await client.SendAsync(Post("13", new StringContent(Event), From));

        Assert.Equal(HttpStatusCode.TooManyRequests, sameSender.StatusCode);
        Assert.Contains("ingest_busy", await sameSender.Content.ReadAsStringAsync()); // not the rate limiter's
        Assert.Equal(HttpStatusCode.OK, sameOrganisationElsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherOrganisation.StatusCode);
        held.Finish();
        Assert.Equal(HttpStatusCode.OK, (await holding).StatusCode);
        // Each request gives its sender's share back once it is answered.
        await WaitUntilAsync(() => Task.FromResult(budget.SharesHeld == 0));
    }

    private const string From = "198.51.100.1";

    // The relay sits behind a trusted proxy (CONDUX_TRUSTED_PROXIES above), which names the client in
    // X-Forwarded-For.
    private sealed class BehindAProxy : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    [Fact]
    public async Task A_body_that_stops_arriving_is_aborted_and_gives_its_share_back()
    {
        const long size = 1024 * 1024;
        var budget = new IngestBodyBudget(size, size, size);
        var clock = new FakeTimeProvider();
        var quota = new RecordingQuotaMeter();
        var client = Client(budget, clock, quota);
        var stalled = new UnfinishedBody(3000);

        var request = client.SendAsync(Post("11", stalled));
        await stalled.Sent.Task;
        await WaitUntilAsync(() => Task.FromResult(IsCharged(budget, size, 1)));

        clock.Advance(StalledBodyWatch.Limit + TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<Exception>(() => request);
        await WaitUntilAsync(() => Task.FromResult(!IsCharged(budget, size, 1) && budget.SharesHeld == 0));
        // Project 11 is on a capped tier, and the aborted body stored nothing, so its event is given back.
        await WaitUntilAsync(() => Task.FromResult(quota.Refunded == 1));
    }

    private sealed class RecordingQuotaMeter : IQuotaMeter
    {
        private int _refunded;

        public int Refunded => Volatile.Read(ref _refunded);

        public ValueTask<QuotaDecision> TryConsumeAsync(
            string key, long monthlyLimit, long count = 1, CancellationToken ct = default) =>
            ValueTask.FromResult(new QuotaDecision(Admitted: count, Used: count, Remaining: 9));

        public ValueTask RefundAsync(string key, long count, CancellationToken ct = default)
        {
            Interlocked.Add(ref _refunded, (int)count);
            return ValueTask.CompletedTask;
        }
    }

    // The limit runs from the body's last byte, not its first: a body that keeps arriving, however slowly,
    // outlives the limit and is answered.
    [Fact]
    public async Task A_body_that_keeps_arriving_is_not_aborted()
    {
        const long size = 1024 * 1024;
        var budget = new IngestBodyBudget(size, size, size);
        var clock = new FakeTimeProvider();
        var client = Client(budget, clock);
        var body = new UnfinishedBody(1000);
        var almost = StalledBodyWatch.Limit - TimeSpan.FromSeconds(1);

        var request = client.SendAsync(Post("11", body));
        await body.Sent.Task;
        // A read is charged its first pooled piece and then each byte it reads.
        await WaitUntilAsync(() => Task.FromResult(IsCharged(budget, size, RequestBodies.FirstPieceBytes + 1000)));
        clock.Advance(almost);
        body.SendMore(500);
        await WaitUntilAsync(() => Task.FromResult(IsCharged(budget, size, RequestBodies.FirstPieceBytes + 1500)));
        clock.Advance(almost);
        body.Finish();

        Assert.Equal(HttpStatusCode.OK, (await request).StatusCode);
    }

    private static HttpRequestMessage Post(string projectId, HttpContent content, string client = From) =>
        new(HttpMethod.Post, $"/api/{projectId}/store/")
        {
            Content = content,
            Headers = { { "x-condux-auth", "devkey" }, { "X-Forwarded-For", client } },
        };

    // Whether at least `bytes` of the budget are charged, read by asking for the rest and giving it back.
    private static bool IsCharged(IngestBodyBudget budget, long size, long bytes)
    {
        if (!budget.TryReserve(size - bytes + 1))
        {
            return true;
        }
        budget.Release(size - bytes + 1);
        return false;
    }

    // Waits for a state, never for a time: the deadline only stops a broken test from hanging.
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Delay(10);
        }
    }

    // A store body that sends its first bytes, then more only when the test says, until it is finished.
    private sealed class UnfinishedBody(int firstBytes) : HttpContent
    {
        private readonly Channel<string?> _more = Channel.CreateUnbounded<string?>();

        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SendMore(int bytes) => _more.Writer.TryWrite(new string('a', bytes));

        public void Finish() => _more.Writer.TryWrite(null);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"message\":\"" + new string('a', firstBytes)));
            await stream.FlushAsync();
            Sent.TrySetResult();
            await foreach (var more in _more.Reader.ReadAllAsync())
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(more ?? "\",\"level\":\"error\"}"));
                await stream.FlushAsync();
                if (more is null)
                {
                    return;
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
