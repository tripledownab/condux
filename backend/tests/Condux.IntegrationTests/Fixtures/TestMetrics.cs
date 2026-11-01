using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Condux.Storage.SourceMaps;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Instruments for a test that builds its services by hand, each on a meter factory of its own, so a test
/// listening to one cannot count another's measurements.
/// </summary>
public static class TestMetrics
{
    public static IMeterFactory ToMeterFactory() =>
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    public static SymbolicationMetrics ToSymbolicationMetrics() => new(ToMeterFactory());

    /// <summary>
    /// Symbolication instruments together with every measurement they take, by instrument name, with the
    /// <c>pass</c> tag where there is one.
    /// </summary>
    public sealed class SymbolicationRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();

        public SymbolicationRecorder()
        {
            var factory = ToMeterFactory();
            Metrics = new SymbolicationMetrics(factory);
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == factory && instrument.Meter.Name == SymbolicationMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i.Name, v, tags));
            _listener.Start();
        }

        public SymbolicationMetrics Metrics { get; }

        public ConcurrentQueue<(string Instrument, double Value, string? Pass)> Measurements { get; } = new();

        private void Add(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? pass = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "pass")
                {
                    pass = (string?)tag.Value;
                }
            }
            Measurements.Enqueue((instrument, value, pass));
        }

        public void Dispose() => _listener.Dispose();
    }
}
