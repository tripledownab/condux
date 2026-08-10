using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.Sdk;

/// <summary>
/// Reports errors to a Condux relay. Emits the Sentry "store" wire shape so the relay normalizes it exactly
/// like an official Sentry SDK — point it at a project DSN and it works. Delivery is resilient (429 / 5xx /
/// network failures are retried with backoff, honoring <c>Retry-After</c>) and <b>never throws</b>: a failed
/// send returns a <see cref="SendResult"/> rather than crashing the host app. One client is cheap to hold
/// for the app's lifetime. Inspired by common SDK transports, implemented fresh.
/// </summary>
public sealed class ConduxClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConduxOptions options;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<TimeSpan, CancellationToken, Task> sleep;
    private readonly string storeUrl;
    private readonly string publicKey;

    /// <summary>Create a client for the given options. Throws on a malformed DSN (a config error that
    /// should fail loud at startup, not be swallowed per capture).</summary>
    public ConduxClient(ConduxOptions options)
    {
        this.options = options;
        var dsn = Dsn.Parse(options.Dsn);
        storeUrl = $"{dsn.Endpoint}/api/{dsn.ProjectId}/store/";
        publicKey = dsn.PublicKey;
        http = new HttpClient(options.Transport ?? new HttpClientHandler());
        clock = options.Clock ?? (() => DateTimeOffset.UtcNow);
        sleep = options.Sleep ?? Task.Delay;
    }

    /// <summary>
    /// Report an exception as an error-level event, with its stack trace. Pass <paramref name="handled"/>
    /// = false for an uncaught exception (a framework integration does this) so the relay marks it unhandled.
    /// </summary>
    public Task<SendResult> CaptureExceptionAsync(
        Exception error, bool handled = true, CancellationToken cancellationToken = default) =>
        DispatchAsync(Level.Error, message: null, StackTraceReader.ToException(error, handled), cancellationToken);

    /// <summary>Report a bare message event at the given level (default <see cref="Level.Info"/>).</summary>
    public Task<SendResult> CaptureMessageAsync(
        string message, Level level = Level.Info, CancellationToken cancellationToken = default) =>
        DispatchAsync(level, message, exception: null, cancellationToken);

    private Task<SendResult> DispatchAsync(
        Level level, string? message, SentryException? exception, CancellationToken cancellationToken)
    {
        var payload = new EventPayload
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = clock().ToUnixTimeMilliseconds() / 1000.0, // epoch seconds, the store convention
            Platform = "csharp",
            Level = level.ToString().ToLowerInvariant(),
            Environment = options.Environment,
            Release = options.Release,
            Message = message,
            Exception = exception is null ? null : new ExceptionEnvelope { Values = [exception] },
        };

        var body = JsonSerializer.Serialize(payload, Json);
        return EventTransport.SendAsync(http, storeUrl, publicKey, body, options.MaxRetries, sleep, cancellationToken);
    }
}

// A parsed DSN: scheme://<publicKey>@<host>/<projectId>.
internal readonly record struct Dsn(string Endpoint, string ProjectId, string PublicKey)
{
    public static Dsn Parse(string dsn)
    {
        var uri = new Uri(dsn);
        return new Dsn($"{uri.Scheme}://{uri.Authority}", uri.AbsolutePath.TrimStart('/'), uri.UserInfo);
    }
}
