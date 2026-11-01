using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Otlp;
using Condux.Relay.Ingest;
using Xunit;
using OtlpSpan = Condux.Otlp.Span;

namespace Condux.Relay.Tests;

/// <summary>
/// A failed span becomes an event and nothing else in an export does, and a failure that set ERROR on
/// several spans of a chain becomes one event, not one for each. The checked-in exports are what real
/// instrumentations sent when an agent failed (OpenAI, the OpenAI Agents SDK and the MCP SDK against a
/// fake model), with their identifying strings replaced by ones of the same length; the synthetic trees
/// pin the chain rules the captures do not reach.
/// </summary>
public class OtlpSpanMapperTests
{
    private static readonly byte[] Trace = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    private static IReadOnlyList<Event> FromCapture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "traces", $"{name}.protobuf.bin");
        var decoded = OtlpTraces.ParseProtobuf(File.ReadAllBytes(path));
        Assert.True(decoded.IsSuccess);
        return OtlpSpanMapper.ToEvents(decoded.Value);
    }

    // A span with ids made from small numbers, so a tree is written as (id, parent) pairs.
    private static OtlpSpan Span(int id, int? parent, bool failed, params (string Key, string Value)[] attributes)
    {
        var span = new OtlpSpan
        {
            TraceId = Trace,
            SpanId = [0, 0, 0, 0, 0, 0, 0, (byte)id],
            ParentSpanId = parent is { } p ? [0, 0, 0, 0, 0, 0, 0, (byte)p] : [],
            Name = $"span-{id}",
            EndTimeUnixNano = 1_700_000_000_000_000_000UL,
            Status = new SpanStatus { Code = failed ? SpanStatusCode.Error : SpanStatusCode.Ok, Message = failed ? "it failed" : "" },
        };
        foreach (var (key, value) in attributes)
        {
            span.Attributes.Add(new KeyValue { Key = key, Value = new AnyValue { Kind = AnyValueKind.String, StringValue = value } });
        }

        return span;
    }

    private static ExportTraceServiceRequest Export(params OtlpSpan[] spans)
    {
        var scope = new ScopeSpans();
        scope.Spans.AddRange(spans);
        var resource = new ResourceSpans();
        resource.ScopeSpans.Add(scope);
        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(resource);
        return request;
    }

    private static List<string> Names(IReadOnlyList<Event> events) => [.. events.Select(e => e.Transaction!)];

    // ---- what real instrumentations sent ----

    [Fact]
    public void A_tool_that_raises_is_one_event_though_the_agent_above_it_failed_too()
    {
        // execute_tool and invoke_agent both carry ERROR for this one failure.
        var e = Assert.Single(FromCapture("agents-tool-raises"));

        Assert.Equal(Level.Error, e.Level);
        Assert.Contains("execute_tool", e.Fingerprint);
        Assert.Contains("boom", e.Fingerprint);
        // The tool span itself says "OpenAI Agent", the instrumentation's placeholder; the run above it
        // carries the agent's real name.
        Assert.Equal("checkout-agent", e.Tags["gen_ai.agent.name"]);
    }

    [Fact]
    public void A_failed_model_call_is_a_warning_and_one_event()
    {
        var e = Assert.Single(FromCapture("agents-model-500"));

        Assert.Equal(Level.Warning, e.Level);
        Assert.Contains("chat", e.Fingerprint);
    }

    [Fact]
    public void A_handled_tool_error_is_reported_though_the_agent_span_stayed_ok()
    {
        var e = Assert.Single(FromCapture("agents-tool-handled"));

        Assert.Equal(Level.Error, e.Level);
        Assert.Contains("execute_tool", e.Fingerprint);
        Assert.Equal("checkout-agent", e.Tags["gen_ai.agent.name"]);
    }

    [Fact]
    public void An_mcp_tool_error_groups_on_its_error_type_and_tool_name()
    {
        var e = Assert.Single(FromCapture("mcp-tool-error"));

        Assert.Equal("tool_error", e.Exceptions[0].Type);
        Assert.Contains("refuses", e.Fingerprint);
    }

    [Fact]
    public void A_provider_rate_limit_is_a_warning_typed_by_its_error_type()
    {
        var e = Assert.Single(FromCapture("openai-429"));

        Assert.Equal("RateLimitError", e.Exceptions[0].Type);
        Assert.Equal(Level.Warning, e.Level);
    }

    [Fact]
    public void The_exception_event_names_the_type_and_the_text_when_the_span_carries_one()
    {
        var e = Assert.Single(FromCapture("stacktrace-429"));

        Assert.Equal("openai.RateLimitError", e.Exceptions[0].Type);
        Assert.Contains("Rate limit", e.Exceptions[0].Value);
    }

    [Theory]
    [InlineData("agents-tool-raises")]
    [InlineData("agents-model-500")]
    [InlineData("agents-tool-handled")]
    [InlineData("stacktrace-429")]
    public void No_prompt_or_tool_argument_reaches_the_event(string capture)
    {
        // The captured prompts are these three phrases. The error text of the scenario that raises says
        // "secret detail" on purpose and is kept: it is error text, scrubbed like any exception message.
        string[] prompts = ["secret system prompt", "secret user prompt", "secret prompt text"];
        foreach (var e in FromCapture(capture))
        {
            var everything = string.Join('\n', e.Tags.Select(kv => $"{kv.Key}={kv.Value}"))
                + e.Exceptions[0].Value + e.Message + e.Transaction;
            Assert.All(prompts, prompt => Assert.DoesNotContain(prompt, everything, StringComparison.Ordinal));
            Assert.DoesNotContain(e.Tags.Keys, key => key.Contains("messages", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_capture_really_carried_the_prompt_the_event_drops()
    {
        // Without this the test above would pass over a capture that never held a prompt.
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "traces", "agents-tool-raises.protobuf.bin");
        var bytes = File.ReadAllBytes(path);

        Assert.True(System.Text.Encoding.UTF8.GetString(bytes).Contains("secret user prompt", StringComparison.Ordinal));
    }

    // ---- the chain rules ----

    [Fact]
    public void A_chain_of_failures_reports_only_the_deepest()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: true), Span(2, 1, failed: true), Span(3, 2, failed: true)));

        Assert.Equal(["span-3"], Names(events));
    }

    [Fact]
    public void A_failure_under_an_ok_span_still_hides_the_failed_span_above_it()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: true), Span(2, 1, failed: false), Span(3, 2, failed: true)));

        Assert.Equal(["span-3"], Names(events));
    }

    [Fact]
    public void Two_failures_under_one_parent_are_two_events()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: true), Span(2, 1, failed: true), Span(3, 1, failed: true)));

        Assert.Equal(["span-2", "span-3"], Names(events).Order().ToList());
    }

    [Fact]
    public void A_failure_whose_parent_is_not_in_the_export_is_reported()
    {
        var events = OtlpSpanMapper.ToEvents(Export(Span(2, 1, failed: true)));

        Assert.Equal(["span-2"], Names(events));
    }

    [Fact]
    public void Spans_that_did_not_fail_are_not_events()
    {
        Assert.Empty(OtlpSpanMapper.ToEvents(Export(Span(1, null, failed: false), Span(2, 1, failed: false))));
    }

    [Fact]
    public void A_parent_chain_that_loops_ends_instead_of_spinning()
    {
        // A sender can write any parent id. Every span of the loop is an ancestor of another, so none is the
        // deepest and the failure is reported nowhere: the cost of a malformed export is its own.
        var events = OtlpSpanMapper.ToEvents(Export(Span(1, 2, failed: true), Span(2, 1, failed: true)));

        Assert.Empty(events);
    }

    [Fact]
    public void A_span_sent_twice_in_one_export_is_reported_once()
    {
        var events = OtlpSpanMapper.ToEvents(Export(Span(1, null, failed: true), Span(1, null, failed: true)));

        Assert.Single(events);
    }

    [Fact]
    public void An_export_sent_twice_gives_the_same_event_ids()
    {
        var first = OtlpSpanMapper.ToEvents(Export(Span(1, null, failed: true)));
        var second = OtlpSpanMapper.ToEvents(Export(Span(1, null, failed: true)));

        Assert.Equal(first[0].EventId, second[0].EventId);
        Assert.Equal(32, first[0].EventId.Length);
    }

    // ---- what an event is keyed on and made of ----

    [Fact]
    public void The_error_text_is_not_part_of_what_an_issue_is_keyed_on()
    {
        var one = Span(1, null, failed: true, ("gen_ai.operation.name", "execute_tool"), ("gen_ai.tool.name", "boom"));
        var other = Span(2, null, failed: true, ("gen_ai.operation.name", "execute_tool"), ("gen_ai.tool.name", "boom"));
        other.Status!.Message = "a different message with an id 8841";

        var a = Fingerprinter.Compute(OtlpSpanMapper.ToEvents(Export(one))[0]);
        var b = Fingerprinter.Compute(OtlpSpanMapper.ToEvents(Export(other))[0]);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Failures_of_different_tools_are_different_issues()
    {
        var boom = Span(1, null, failed: true, ("gen_ai.operation.name", "execute_tool"), ("gen_ai.tool.name", "boom"));
        var bang = Span(2, null, failed: true, ("gen_ai.operation.name", "execute_tool"), ("gen_ai.tool.name", "bang"));

        var a = Fingerprinter.Compute(OtlpSpanMapper.ToEvents(Export(boom))[0]);
        var b = Fingerprinter.Compute(OtlpSpanMapper.ToEvents(Export(bang))[0]);

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void Who_was_affected_fills_the_user_and_leaves_the_tags_as_it_does_for_a_log()
    {
        // The raw identifier must not be stored as a tag: the user is what the scrub turns into a
        // pseudonymous key and a redacted address.
        var span = Span(1, null, failed: true, ("enduser.id", "u-42"), ("user.email", "a@example.com"));

        var e = OtlpSpanMapper.ToEvents(Export(span))[0];

        Assert.Equal("u-42", e.User?.Id);
        Assert.Equal("a@example.com", e.User?.Email);
        Assert.DoesNotContain("enduser.id", e.Tags.Keys);
        Assert.DoesNotContain("user.email", e.Tags.Keys);
    }

    [Fact]
    public void The_agent_a_failure_happened_under_is_named_from_the_failed_span_above_it()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: true, ("gen_ai.agent.name", "checkout-agent")),
            Span(2, 1, failed: true)));

        Assert.Equal("checkout-agent", Assert.Single(events).Tags["gen_ai.agent.name"]);
    }

    [Fact]
    public void The_agent_run_above_a_failure_names_it_whether_or_not_the_run_failed()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: false, ("gen_ai.operation.name", "invoke_agent"), ("gen_ai.agent.name", "checkout-agent")),
            Span(2, 1, failed: false, ("gen_ai.agent.name", "Somewhere in between")),
            Span(3, 2, failed: true, ("gen_ai.agent.name", "OpenAI Agent"))));

        // The run's name wins over the failed span's own placeholder and over a nearer span that is not a run.
        Assert.Equal("checkout-agent", Assert.Single(events).Tags["gen_ai.agent.name"]);
    }

    [Fact]
    public void Without_an_agent_run_above_it_a_span_keeps_its_own_agent_name()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: false, ("gen_ai.agent.name", "outer")),
            Span(2, 1, failed: true, ("gen_ai.agent.name", "inner"))));

        Assert.Equal("inner", Assert.Single(events).Tags["gen_ai.agent.name"]);
    }

    [Fact]
    public void A_span_that_is_not_an_agent_run_keeps_its_key_whatever_agent_is_found_above_it()
    {
        // A failed request span inside an agent run: the run's name is on its page, not in its key, so the
        // issue it already had is the one it lands in.
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: false, ("gen_ai.operation.name", "invoke_agent"), ("gen_ai.agent.name", "checkout-agent")),
            Span(2, 1, failed: true, ("http.request.method", "GET"))));

        var e = Assert.Single(events);
        Assert.Equal("checkout-agent", e.Tags["gen_ai.agent.name"]);
        Assert.Equal(["otlp-span", "", "span:span-2", ""], e.Fingerprint);
    }

    [Fact]
    public void An_empty_agent_name_is_no_name_and_an_unnamed_run_borrows_none()
    {
        var events = OtlpSpanMapper.ToEvents(Export(
            Span(1, null, failed: false, ("gen_ai.operation.name", "invoke_agent"), ("gen_ai.agent.name", "outer")),
            Span(2, 1, failed: true, ("gen_ai.operation.name", "invoke_agent"), ("gen_ai.agent.name", ""),
                ("gen_ai.request.model", "m"))));

        var e = Assert.Single(events);
        Assert.DoesNotContain("gen_ai.agent.name", e.Tags.Keys);
        Assert.Contains("m", e.Fingerprint);
        Assert.DoesNotContain("outer", e.Fingerprint);
    }

    [Fact]
    public async Task Looking_for_the_agent_ends_on_a_parent_id_that_loops()
    {
        // Spans 1 and 2 name each other as parent and neither is an agent run, so only the walk's own bound
        // ends the search for the failure hanging off them. A sender can write this.
        var export = Export(
            Span(1, 2, failed: false),
            Span(2, 1, failed: false),
            Span(3, 1, failed: true));

        var events = await Task.Run(() => OtlpSpanMapper.ToEvents(export)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(events);
    }

    [Fact]
    public void A_failed_agent_run_is_keyed_on_its_agent_not_on_its_model()
    {
        // Two agents on one model, failing with the same error type, are two issues.
        static OtlpSpan Run(int id, string agent) => Span(id, null, failed: true,
            ("gen_ai.operation.name", "invoke_agent"), ("gen_ai.agent.name", agent),
            ("gen_ai.request.model", "shared-model"), ("error.type", "AgentError"));

        var events = OtlpSpanMapper.ToEvents(Export(Run(1, "checkout-agent"), Run(2, "refund-agent")));

        Assert.Equal(2, events.Count);
        Assert.Contains("checkout-agent", events[0].Fingerprint);
        Assert.Contains("refund-agent", events[1].Fingerprint);
        Assert.DoesNotContain("shared-model", events[0].Fingerprint);
    }

    [Fact]
    public void A_span_merely_named_like_a_model_call_is_still_an_error()
    {
        var span = Span(1, null, failed: true);
        span.Name = "chat";

        Assert.Equal(Level.Error, OtlpSpanMapper.ToEvents(Export(span))[0].Level);
    }

    [Fact]
    public void Only_attributes_known_to_describe_the_operation_are_kept()
    {
        // Prompts, queries, URLs and who the user was can all ride on a span, under names no list of
        // content could have anticipated. None of these is a name the code has seen.
        var span = Span(1, null, failed: true,
            ("gen_ai.tool.name", "boom"),
            ("error.type", "ToolError"),
            ("http.route", "/orders/{id}"),
            ("gen_ai.input.messages", "secret one"),
            ("input.value", "secret two"),
            ("tool_arguments", "secret three"),
            ("db.query.text", "select * from users where email = 'a@example.com'"),
            ("gen_ai.retrieval.query.text", "secret four"),
            ("user.full_name", "Jane Doe"),
            ("session.id", "s-1"),
            ("url.full", "https://example.com/reset?token=abc"));

        var tags = OtlpSpanMapper.ToEvents(Export(span))[0].Tags;

        Assert.Equal(["error.type", "gen_ai.tool.name", "http.route"], tags.Keys.Order().ToList());
    }

    [Fact]
    public void A_failed_span_that_names_itself_as_its_parent_still_reports()
    {
        var events = OtlpSpanMapper.ToEvents(Export(Span(1, 1, failed: true)));

        Assert.Single(events);
    }

    [Fact]
    public void Spans_without_usable_ids_still_report_each_one()
    {
        var one = Span(1, null, failed: true);
        var two = Span(2, null, failed: true);
        one.SpanId = [];
        two.SpanId = [];

        var events = OtlpSpanMapper.ToEvents(Export(one, two));

        Assert.Equal(2, events.Count);
        Assert.NotEqual(events[0].EventId, events[1].EventId);
    }

    [Fact]
    public void An_id_of_the_wrong_length_is_not_used_to_build_a_key()
    {
        // The id is the sender's to size, and a key is built by hex-encoding it.
        var span = Span(1, null, failed: true);
        span.TraceId = new byte[100_000];
        span.TraceId[0] = 1;

        var e = Assert.Single(OtlpSpanMapper.ToEvents(Export(span)));

        Assert.Null(e.TraceId);
        Assert.Equal(32, e.EventId.Length);
    }

    [Fact]
    public void Events_past_the_limit_are_counted_and_not_built()
    {
        var spans = new List<OtlpSpan> { Span(1, null, failed: false) };
        spans.AddRange(Enumerable.Range(2, 5).Select(i => Span(i, 1, failed: true)));

        var events = OtlpSpanMapper.ToEvents(Export([.. spans]), limit: 2, out var surplus);

        Assert.Equal(2, events.Count);
        Assert.Equal(3, surplus);
    }

    [Fact]
    public void A_span_merely_named_like_an_operation_is_a_different_issue_from_the_operation()
    {
        var named = Span(1, null, failed: true);
        named.Name = "chat";
        var attributed = Span(2, null, failed: true, ("gen_ai.operation.name", "chat"));

        var a = Fingerprinter.Compute(OtlpSpanMapper.ToEvents(Export(named))[0]);
        var b = Fingerprinter.Compute(OtlpSpanMapper.ToEvents(Export(attributed))[0]);

        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
    }
}
