using System.Diagnostics.Metrics;
using System.Net;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// Counts the symbolizer's answers as its callers receive them, by call and status, so a busy symbolizer
/// (429) can be alerted on before anyone reports frames left as addresses. An answer is counted once it
/// has been received in full, so a call that got none (a timeout, a refused connection) or whose body was past the
/// client's size cap is not counted here; its warning is in the log.
/// </summary>
public sealed class SymbolizerMetrics
{
    /// <summary>The meter's name, which the telemetry wiring subscribes to.</summary>
    public const string MeterName = "Condux.Symbolication";

    private readonly Counter<long> _responses;

    public SymbolizerMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _responses = meter.CreateCounter<long>(
            "condux.symbolizer.responses", unit: "{response}",
            description: "Answers from the symbolizer as its callers receive them, by call and HTTP status.");
    }

    public void Record(string call, HttpStatusCode status) =>
        _responses.Add(1, new KeyValuePair<string, object?>("call", call),
            new KeyValuePair<string, object?>("http.response.status_code", (int)status));
}
