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
        DispatchAsync(Level.Error, message: null, error, handled, context: null, cancellationToken);

    /// <summary>
    /// Report an exception with detail belonging to this one event. <paramref name="context"/> carries the
    /// request it happened during and any request-scoped tags; they are passed here rather than set on
    /// <see cref="ConduxScope"/> because scope state outlives the call, so on a server handling requests
    /// concurrently request detail set ambiently attaches to whichever event is captured next.
    /// </summary>
    /// <remarks>An overload rather than an optional parameter, so existing calls that pass a
    /// CancellationToken positionally keep compiling and keep meaning the same thing.</remarks>
    public Task<SendResult> CaptureExceptionAsync(
        Exception error, bool handled, CaptureContext? context,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(Level.Error, message: null, error, handled, context, cancellationToken);

    /// <summary>Report a bare message event at the given level (default <see cref="Level.Info"/>).</summary>
    public Task<SendResult> CaptureMessageAsync(
        string message, Level level = Level.Info, CancellationToken cancellationToken = default) =>
        DispatchAsync(level, message, error: null, handled: true, context: null, cancellationToken);

    /// <summary>Report a message with per-event detail. See the CaptureExceptionAsync overload.</summary>
    public Task<SendResult> CaptureMessageAsync(
        string message, Level level, CaptureContext? context,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(level, message, error: null, handled: true, context, cancellationToken);

    // Reading the exception happens inside the guard below, not at the call site: a custom exception type
    // decides what its own Message and stack yield, so that read is one of the things that can fail.
    private async Task<SendResult> DispatchAsync(
        Level level, string? message, Exception? error, bool handled, CaptureContext? context,
        CancellationToken cancellationToken)
    {
        try
        {
            var exception = error is null ? null : StackTraceReader.ToException(error, handled);
            var scope = ConduxScope.Fields();
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
                User = scope.User,
                Request = context?.Request,
                // Merged over the ambient tags rather than replacing them, so a per-event tag cannot
                // silently drop the deployment-wide ones.
                Tags = MergeTags(scope.Tags, context?.Tags),
                Contexts = scope.Contexts,
                Breadcrumbs = scope.Breadcrumbs,
            };

            var body = JsonSerializer.Serialize(payload, Json);
            return await EventTransport.SendAsync(
                http, storeUrl, publicKey, body, options.MaxRetries, sleep, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller's own cancellation, not a reporting failure
        }
        catch (Exception failure)
        {
            // Reporting never throws: an error monitor that throws turns a handled error into an unhandled
            // one in exactly the code path where someone is already dealing with a failure. Anything the
            // transport does not already handle (an unserializable scope value, a disposed handler) comes
            // back as a failed result instead. Attempts is 0 — the event never left the process.
            return new SendResult(false, 0, null, failure.Message);
        }
    }

    private static IReadOnlyDictionary<string, string>? MergeTags(
        IReadOnlyDictionary<string, string>? ambient, IReadOnlyDictionary<string, string>? perEvent)
    {
        if (perEvent is null || perEvent.Count == 0)
        {
            return ambient;
        }

        var merged = ambient is null ? [] : new Dictionary<string, string>(ambient);
        foreach (var (key, value) in perEvent)
        {
            merged[key] = value;
        }
        return merged;
    }
}

/// <summary>
/// Per-event enrichment, passed at the capture call rather than set ambiently, for anything derived from
/// a single request. See the CaptureExceptionAsync overload for why that distinction matters.
/// </summary>
public sealed record CaptureContext
{
    /// <summary>The request the event happened during.</summary>
    public ConduxRequest? Request { get; init; }

    /// <summary>Tags for this event only, merged over the ambient ones.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

// A parsed DSN: scheme://<publicKey>@<host>/<projectId>.
internal readonly record struct Dsn(string Endpoint, string ProjectId, string PublicKey)
{
    // Throws on anything that is not a whole DSN. A key-less or project-less URL parses as a Uri quite
    // happily, and a client built from one sends every event to an address that can only be rejected —
    // so the check belongs here, where it fails once at startup rather than silently per capture.
    public static Dsn Parse(string dsn)
    {
        var projectId = Uri.TryCreate(dsn, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimStart('/') : "";
        if (uri is null || uri.UserInfo.Length == 0 || projectId.Length == 0)
        {
            // One message for every shape of wrong DSN, naming what a right one looks like. The key in a
            // DSN is the public ingest key, so echoing the value back is not a secret leak.
            throw new FormatException($"Condux: DSN must be scheme://<key>@<host>/<projectId>, got '{dsn}'.");
        }

        return new Dsn($"{uri.Scheme}://{uri.Authority}", projectId, uri.UserInfo);
    }
}
