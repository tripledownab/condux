using System.Diagnostics.Metrics;
using System.Net;

namespace Condux.Storage.SourceMaps;

/// <summary>
/// The instruments of read-time symbolication, all on one meter. The symbolizer's answers are counted as
/// its callers receive them, by call and status, so a busy symbolizer (429) can be alerted on before anyone
/// reports frames left as addresses. An answer is counted once it has been received in full, so a call that
/// got none (a timeout, a refused connection) or whose body was past the client's size cap is not counted
/// there; its warning is in the log. Each call into <see cref="EventSymbolication"/> with a pass to run and
/// events to read is timed, and each failure that left an event as stored is counted by where it happened:
/// the source-map pass, the native pass (including each image the symbolizer or the object store failed
/// on), the pass restoring obfuscated Dart class names, or around the passes. Running out of the budget counts against the pass it interrupted; events a
/// call skipped after that are not counted. So the cost a caller pays waiting for symbols is measured
/// rather than inferred from warnings.
/// </summary>
public sealed class SymbolicationMetrics
{
    /// <summary>The meter's name, which the telemetry wiring subscribes to.</summary>
    public const string MeterName = "Condux.Symbolication";

    /// <summary>The <c>pass</c> a failure is counted under.</summary>
    public const string SourceMapPass = "source-map", NativePass = "native", DartTypesPass = "dart-types",
        EventPass = "event";

    private readonly Counter<long> _responses;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _failures;

    public SymbolicationMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _responses = meter.CreateCounter<long>(
            "condux.symbolizer.responses", unit: "{response}",
            description: "Answers from the symbolizer as its callers receive them, by call and HTTP status.");
        // Buckets in seconds: the SDK's default buckets are sized for milliseconds and would put almost
        // every call in the first one.
        _duration = meter.CreateHistogram<double>(
            "condux.symbolication.duration", unit: "s",
            description: "Time one call spent symbolicating its events, up to its budget.",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10] });
        _failures = meter.CreateCounter<long>(
            "condux.symbolication.failures", unit: "{failure}",
            description: "Symbolication failures that left an event as stored, by pass.");
    }

    public void RecordResponse(string call, HttpStatusCode status) =>
        _responses.Add(1, new KeyValuePair<string, object?>("call", call),
            new KeyValuePair<string, object?>("http.response.status_code", (int)status));

    public void RecordDuration(TimeSpan elapsed) => _duration.Record(elapsed.TotalSeconds);

    public void RecordFailure(string pass) => _failures.Add(1, new KeyValuePair<string, object?>("pass", pass));
}
