namespace Condux.Sdk;

/// <summary>Configuration for a <see cref="ConduxClient"/>.</summary>
public sealed class ConduxOptions
{
    /// <summary>The project DSN, e.g. <c>https://&lt;key&gt;@ingest.condux.ai/&lt;projectId&gt;</c>.</summary>
    public required string Dsn { get; init; }

    /// <summary>Deployment environment tag (e.g. <c>production</c>). Optional.</summary>
    public string? Environment { get; init; }

    /// <summary>Release identifier (e.g. a version or commit). Optional; powers release attribution.</summary>
    public string? Release { get; init; }

    /// <summary>Extra delivery attempts after the first (default 3, so up to 4 attempts).</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Transport handler. Defaults to a plain <see cref="System.Net.Http.HttpClientHandler"/>;
    /// tests inject a stub.</summary>
    public HttpMessageHandler? Transport { get; init; }

    /// <summary>Backoff delay hook. Defaults to <see cref="System.Threading.Tasks.Task.Delay(System.TimeSpan,
    /// System.Threading.CancellationToken)"/>; tests inject a no-op that records the delays.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Sleep { get; init; }

    /// <summary>Clock for the event timestamp. Defaults to <see cref="System.DateTimeOffset.UtcNow"/>.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>
    /// Report the resolved NuGet package versions with each event (ADR-0041), so a security advisory
    /// can be answered with the version actually running rather than the one a project file declares.
    /// On by default.
    ///
    /// <para>Turn it off if the payload cost matters more than the answer. The inventory is read once
    /// when the client is constructed and then repeated on an interval, which is what makes it survive
    /// a dropped or rate-limited event, and also what makes it cost bytes on the ones that carry
    /// it.</para>
    /// </summary>
    public bool SendModules { get; init; } = true;
}
