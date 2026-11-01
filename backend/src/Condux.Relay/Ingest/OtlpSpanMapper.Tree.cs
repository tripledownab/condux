using OtlpSpan = Condux.Otlp.Span;

namespace Condux.Relay.Ingest;

// The span tree of one export: how a span is found by its ids, which spans are above it, and which failed
// spans have a failed span below them. OtlpSpanMapper.cs builds the events from what this finds.
public static partial class OtlpSpanMapper
{
    private const int TraceIdLength = 16;
    private const int SpanIdLength = 8;

    private readonly record struct SpanKey(string Trace, string Span);

    private sealed record Found(OtlpSpan Span, IReadOnlyDictionary<string, string> Resource);

    /// <summary>Walks up from each failed span marking its ancestors, and stops at one already marked,
    /// since everything above it is marked too. That keeps the work to one visit per span, and it ends
    /// on a parent id that loops back, which a sender can write. A chain that loops reports nothing, since
    /// every span in it is the ancestor of another. A span that names itself as its parent has no parent.</summary>
    private static HashSet<SpanKey> MarkAncestorsOfFailures(Dictionary<SpanKey, Found> findable, List<Found> failed)
    {
        var marked = new HashSet<SpanKey>();
        foreach (var f in failed)
        {
            foreach (var ancestor in Ancestors(f.Span, findable))
            {
                if (!marked.Add(KeyOf(ancestor)))
                {
                    break;
                }
            }
        }

        return marked;
    }

    /// <summary>The spans above one in the same export, nearest first. It ends at a parent that is not in
    /// the export or a span that names itself as its parent; a parent id that loops back does not end it, so
    /// each caller bounds the walk.</summary>
    private static IEnumerable<OtlpSpan> Ancestors(OtlpSpan span, Dictionary<SpanKey, Found> findable)
    {
        var current = span;
        while (HasParent(current)
            && findable.TryGetValue(ParentKeyOf(current), out var parent)
            && !ReferenceEquals(parent.Span, current))
        {
            current = parent.Span;
            yield return current;
        }
    }

    /// <summary>Both ids the length the protocol requires, and not all zeroes. Anything else is invalid, and
    /// is not used as a key: a key is built by hex-encoding the id, and the id is the sender's to size.</summary>
    private static bool HasIds(OtlpSpan span)
        => IsId(span.TraceId, TraceIdLength) && IsId(span.SpanId, SpanIdLength);

    private static bool HasParent(OtlpSpan span) => HasIds(span) && IsId(span.ParentSpanId, SpanIdLength);

    private static bool IsId(byte[] id, int length) => id.Length == length && id.Any(b => b != 0);

    private static SpanKey KeyOf(OtlpSpan span)
        => new(Convert.ToHexStringLower(span.TraceId), Convert.ToHexStringLower(span.SpanId));

    private static SpanKey ParentKeyOf(OtlpSpan span)
        => new(Convert.ToHexStringLower(span.TraceId), Convert.ToHexStringLower(span.ParentSpanId));
}
