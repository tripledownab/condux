// What belongs here: the real HTTP endpoint tests deliver to over a loopback socket, and nothing else.
using System.Net;
using System.Net.Sockets;

namespace Condux.Tests.Shared;

/// <summary>One HTTP request as a <see cref="LoopbackHttpReceiver"/> received it.</summary>
internal sealed record CapturedRequest(string Method, string? ContentType, string Body);

/// <summary>
/// A real HTTP endpoint on a free loopback port that captures what a test sends it, for the tests that
/// deliver over a real socket. One copy for all of them, because the hand-written copies each carried the
/// same two flaws. A free port probed and released before binding can be taken by a parallel test in
/// between, so <see cref="Start"/> retries on a fresh port. And .NET's managed HttpListener binds the port
/// again when it is disposed after <c>Stop()</c>, which throws "Address already in use" once another test
/// holds it, so <see cref="Dispose"/> closes it once and nothing calls <c>Stop()</c>.
/// </summary>
internal sealed class LoopbackHttpReceiver : IDisposable
{
    private const int StartAttempts = 10;

    private readonly HttpListener _listener;

    private LoopbackHttpReceiver(HttpListener listener, string url)
    {
        _listener = listener;
        Url = url;
    }

    /// <summary>The URL to send to, ending in the path given to <see cref="Start"/>.</summary>
    public string Url { get; }

    public static LoopbackHttpReceiver Start(string path = "/hook/")
    {
        for (var attempt = 1; ; attempt++)
        {
            var url = $"http://127.0.0.1:{FreePort()}{path}";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try
            {
                listener.Start();
                return new LoopbackHttpReceiver(listener, url);
            }
            catch (HttpListenerException) when (attempt < StartAttempts)
            {
                listener.Abort();
            }
        }
    }

    /// <summary>
    /// The next request, read in full and answered 200. Call it BEFORE sending, since the sender waits for
    /// the answer, and bound it AFTER the send returns (<c>await next.WaitAsync(timeout)</c>), so the timeout
    /// covers the whole capture and none of the sender's own work.
    /// </summary>
    public async Task<CapturedRequest> NextRequestAsync()
    {
        var context = await _listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        var captured = new CapturedRequest(
            context.Request.HttpMethod, context.Request.ContentType, await reader.ReadToEndAsync());
        context.Response.StatusCode = 200;
        context.Response.Close();
        return captured;
    }

    public void Dispose() => ((IDisposable)_listener).Dispose();

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
