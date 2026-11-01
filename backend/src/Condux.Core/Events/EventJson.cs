using System.Text.Json;
using Condux.Core.Text;

namespace Condux.Core.Events;

/// <summary>
/// The one JSON form of an <see cref="Event"/>: the Kafka message the relay publishes and the consumer
/// reads, and the payload stored in ClickHouse. Every string comes out storable
/// (<see cref="StorableText"/>), so a U+0000 a sender put anywhere in an event cannot reach Postgres from
/// the consumer or from a read of a stored event. Otherwise the serializer's defaults, which both sides
/// used before. EventJsonScanTest fails on an event read from JSON any other way: the read is where
/// the type is named, and it replaces whatever a writer let through.
/// </summary>
public static class EventJson
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new StorableStringConverter() } };

    public static string ToJson(Event e) => JsonSerializer.Serialize(e, Options);

    public static Event? ToEvent(string json) => JsonSerializer.Deserialize<Event>(json, Options);
}
