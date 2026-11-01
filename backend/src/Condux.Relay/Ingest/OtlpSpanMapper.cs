using System.Security.Cryptography;
using Condux.Core.Events;
using Condux.Otlp;
using OtlpSpan = Condux.Otlp.Span;

namespace Condux.Relay.Ingest;

/// <summary>
/// Maps a decoded OTLP traces export onto <see cref="Event"/>s: a failed span becomes an event, and
/// nothing else in the export does. This is how an agent's failures reach us, since the instrumentations
/// measured for it report a failure as an error span and never as a log record.
/// </summary>
/// <remarks>
/// A failure sets ERROR on its own span and on the agent span above it, so every failed span reported
/// would make one failure two events. Only the deepest failed span of a chain is reported: one with no
/// failed descendant in the same export. Each export is judged alone, so a parent sent in a later request
/// than its child reports as well. A span is found by its trace and span id; one whose ids are not the
/// lengths the protocol requires cannot be found, so it is never an ancestor, but it still reports if it
/// failed.
/// </remarks>
public static partial class OtlpSpanMapper
{
    private const string Operation = "gen_ai.operation.name";
    private const string AgentName = "gen_ai.agent.name";
    private const string ToolName = "gen_ai.tool.name";
    private const string ModelName = "gen_ai.request.model";
    private const string InvokeAgent = "invoke_agent";

    // Bounds how far up the spans a failure looks for the agent it belongs to.
    private const int MaxAncestors = 32;

    // A failed model call is the provider's or the account's to fix (a rate limit, a rejected key, an
    // outage), not a defect in the application's code, so it must not start a Conductor run. The
    // operation names are well-known values of the OpenTelemetry GenAI semantic conventions.
    private static readonly HashSet<string> ModelCalls =
        new(["chat", "text_completion", "embeddings", "generate_content"], StringComparer.Ordinal);

    public static IReadOnlyList<Event> ToEvents(ExportTraceServiceRequest request)
        => ToEvents(request, long.MaxValue, out _);

    /// <param name="limit">How many events to build. The rest are counted and not built, since the caller
    /// stores no more than that and the export's size is the sender's to choose.</param>
    /// <param name="surplus">How many events past <paramref name="limit"/> the export held.</param>
    public static IReadOnlyList<Event> ToEvents(ExportTraceServiceRequest request, long limit, out long surplus)
    {
        var findable = new Dictionary<SpanKey, Found>();
        var failed = new List<Found>();
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var resource = OtlpValues.ToAttributeMap(resourceSpans.Resource?.Attributes);
            foreach (var span in resourceSpans.ScopeSpans.SelectMany(scope => scope.Spans))
            {
                var found = new Found(span, resource);
                // A span sent twice in one export is one span: the first is kept, so it reports once.
                // One with ids that cannot be looked up is kept as it is, for it can only be a leaf.
                var isNew = !HasIds(span) || findable.TryAdd(KeyOf(span), found);
                if (isNew && span.Status?.Code == SpanStatusCode.Error)
                {
                    failed.Add(found);
                }
            }
        }

        var hasFailedDescendant = MarkAncestorsOfFailures(findable, failed);
        var deepest = failed.Where(f => !HasIds(f.Span) || !hasFailedDescendant.Contains(KeyOf(f.Span))).ToList();
        surplus = Math.Max(0, deepest.Count - limit);
        return deepest.Take((int)Math.Min(limit, int.MaxValue)).Select(f => BuildEvent(f, findable)).ToList();
    }

    private static Event BuildEvent(Found found, Dictionary<SpanKey, Found> findable)
    {
        var span = found.Span;
        var attributes = OtlpValues.ToAttributeMap(span.Attributes);
        var exception = span.Events.FirstOrDefault(e => e.Name == "exception") is { } e
            ? OtlpValues.ToAttributeMap(e.Attributes)
            : new Dictionary<string, string>();

        // A span merely named like an operation is not taken for one: the level reads the attribute alone,
        // and the fingerprint keeps the two apart.
        var operationAttribute = attributes.GetValueOrDefault(Operation);
        var operation = operationAttribute ?? $"span:{span.Name}";
        var agent = ToAgentName(span, attributes, findable);
        // An agent run is keyed on the agent: it carries the model it asked for too, and keying on that
        // made two agents on one model with the same error one issue. Any other span keeps the key it had,
        // its own agent name included, so a name found above it moves no issue.
        var subject = (operationAttribute == InvokeAgent ? agent : null)
            ?? attributes.GetValueOrDefault(ToolName)
            ?? attributes.GetValueOrDefault(ModelName)
            ?? attributes.GetValueOrDefault(AgentName)
            ?? "";
        var type = FirstNonEmpty(exception.GetValueOrDefault("exception.type"), attributes.GetValueOrDefault("error.type"));
        var named = operationAttribute ?? span.Name;
        var label = subject.Length > 0 ? $"{named} {subject}" : named;
        var text = FirstNonEmpty(exception.GetValueOrDefault("exception.message"), span.Status?.Message);

        var tags = attributes.Where(kv => OtlpSpanTags.IsKept(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (agent is not null)
        {
            tags[AgentName] = agent;
        }
        else
        {
            tags.Remove(AgentName);
        }

        var resource = found.Resource;
        return new Event
        {
            EventId = ToEventId(span),
            TimestampUnixMs = OtlpValues.ToMilliseconds(span.EndTimeUnixNano)
                ?? OtlpValues.ToMilliseconds(span.StartTimeUnixNano) ?? 0,
            Platform = resource.GetValueOrDefault("telemetry.sdk.language"),
            Level = ToLevel(operationAttribute),
            Transaction = string.IsNullOrEmpty(span.Name) ? null : span.Name,
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = type ?? label,
                    Value = text,
                    Stacktrace = OtlpStacktrace.ParseV8(exception.GetValueOrDefault("exception.stacktrace")),
                },
            ],
            // What the issue is keyed on, set here because a frameless event would otherwise group on its
            // exception type alone. The error text is not in it: it carries the variable part of a message.
            Fingerprint = ["otlp-span", type ?? "", operation, subject],
            ServerName = resource.GetValueOrDefault("service.name"),
            Release = resource.GetValueOrDefault("service.version"),
            Environment = resource.GetValueOrDefault("deployment.environment")
                ?? resource.GetValueOrDefault("deployment.environment.name"),
            Tags = tags,
            User = OtlpValues.ToUser(attributes),
            TraceId = span.TraceId.Length == TraceIdLength ? OtlpValues.ToTraceId(span.TraceId) : null,
        };
    }

    /// <summary>The agent a failure happened under. An agent run names itself. Anything else takes the name
    /// from the nearest agent run above it, failed or not: the OpenAI Agents instrumentation puts its own
    /// placeholder ("OpenAI Agent") on every tool and model span, and the agent's real name only on the run.
    /// Without a run above it, the span's own name, then the nearest span above it that names one. An empty
    /// name is no name, and a run without one does not borrow the name of a run around it.</summary>
    private static string? ToAgentName(
        OtlpSpan span, IReadOnlyDictionary<string, string> attributes, Dictionary<SpanKey, Found> findable)
    {
        var own = FirstNonEmpty(attributes.GetValueOrDefault(AgentName));
        if (attributes.GetValueOrDefault(Operation) == InvokeAgent)
        {
            return own;
        }

        string? nearestNamed = null;
        foreach (var ancestor in Ancestors(span, findable).Take(MaxAncestors))
        {
            var name = AttributeOf(ancestor, AgentName);
            if (name is not null && AttributeOf(ancestor, Operation) == InvokeAgent)
            {
                return name;
            }

            nearestNamed ??= name;
        }

        return own ?? nearestNamed;
    }

    private static string? AttributeOf(OtlpSpan span, string key)
        => span.Attributes.FirstOrDefault(kv => kv.Key == key)?.Value is { } value
            ? FirstNonEmpty(OtlpValues.ToText(value))
            : null;

    /// <summary>Failed model calls are warnings; anything else that failed is an error. Read from the
    /// operation attribute alone. A model call from an instrumentation that does not set it is an error.</summary>
    private static Level ToLevel(string? operation) =>
        operation is not null && ModelCalls.Contains(operation) ? Level.Warning : Level.Error;

    /// <summary>Derived from the span's own ids, so an export sent twice stores the same event ids. A span
    /// with no usable ids has nothing to derive from.</summary>
    private static string ToEventId(OtlpSpan span)
        => HasIds(span)
            ? Convert.ToHexStringLower(SHA256.HashData([.. span.TraceId, .. span.SpanId]))[..32]
            : Guid.NewGuid().ToString("N");

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
