using System.Globalization;
using System.Net;
using Condux.Agent.ManagedAgents;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The sweep that removes the Managed Agents sessions a fix run left at the vendor, each holding a checkout
/// of the customer's repository. The vendor deletes a session only once it is no longer running, so the
/// sweep deletes the settled ones and interrupts the rest, and it touches only the sessions of the agents
/// the gateway provisions. Stub HTTP only; CI never calls the real vendor.
/// </summary>
public sealed class ManagedAgentsSessionSweeperTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    private const string Agents = "GET /v1/agents?limit=100&include_archived=true";

    private static string Sessions(string agentId, string statuses) =>
        $"GET /v1/sessions?agent_id={agentId}&limit=100&order=asc&created_at%5Blt%5D=2026-06-15T10%3A00%3A00Z{statuses}";

    private static readonly string Settled = Sessions("agent_ours", "&statuses%5B%5D=idle&statuses%5B%5D=terminated");
    private static readonly string Active = Sessions("agent_ours", "&statuses%5B%5D=running&statuses%5B%5D=rescheduling");

    // Every status, for the pass over statuses this version does not know: a listing filtered to the
    // known ones would never return such a session.
    private static readonly string Unfiltered = Sessions("agent_ours", "");

    private static (ManagedAgentsSessionSweeper Sweeper, StubHttpHandler Http) Create(string? pinnedAgent = null)
    {
        var http = new StubHttpHandler();
        var options = new ManagedAgentsOptions("test-key", 5m) { AgentId = pinnedAgent };
        var client = new ManagedAgentsClient(new HttpClient(http), options);
        http.Routes[Agents] = (HttpStatusCode.OK,
            """{"data":[{"id":"agent_ours","name":"condux-conductor-claude-opus-4-8","archived_at":null},{"id":"agent_theirs","name":"support-bot","archived_at":null}]}""");
        http.Routes[Settled] = (HttpStatusCode.OK, """{"data":[]}""");
        http.Routes[Active] = (HttpStatusCode.OK, """{"data":[]}""");
        http.Routes[Unfiltered] = (HttpStatusCode.OK, """{"data":[]}""");
        return (new ManagedAgentsSessionSweeper(client, options), http);
    }

    private static string Session(string id, string status, string title) =>
        $$"""{"id":"{{id}}","status":"{{status}}","title":"{{title}}"}""";

    [Fact]
    public async Task Deletes_its_own_settled_sessions_and_leaves_everyone_elses()
    {
        var (sweeper, http) = Create();
        http.Routes[Settled] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_ours", "idle", "condux fix 42")}},{{Session("sesn_retitled", "idle", "nightly report")}},{{Session("sesn_done", "terminated", "condux fix 7")}}]}""");
        http.Routes["DELETE /v1/sessions/sesn_ours"] = (HttpStatusCode.OK, "{}");
        http.Routes["DELETE /v1/sessions/sesn_done"] = (HttpStatusCode.OK, "{}");

        var (deleted, interrupted, _) = await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal((2, 0), (deleted, interrupted));
        Assert.DoesNotContain(http.Requests, r => r.Key.Contains("sesn_retitled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Asks_the_vendor_only_for_its_own_agents_sessions()
    {
        // Filtering server-side is what stops other sessions under the key from filling every page it reads.
        var (sweeper, http) = Create();

        await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal([Agents, Settled, Active, Unfiltered], http.Requests.Select(r => r.Key));
    }

    [Fact]
    public async Task Finds_its_agent_on_a_later_page_of_agents()
    {
        // A page holds 20 agents by default, so a key with more would hide ours behind the first page.
        var (sweeper, http) = Create();
        http.Routes[Agents] = (HttpStatusCode.OK,
            """{"data":[{"id":"agent_theirs","name":"support-bot","archived_at":null}],"next_page":"p2"}""");
        http.Routes[Agents + "&page=p2"] = (HttpStatusCode.OK,
            """{"data":[{"id":"agent_ours","name":"condux-conductor-claude-opus-4-8","archived_at":null}]}""");

        await sweeper.SweepAsync(Cutoff, default);

        Assert.Contains(http.Requests, r => r.Key == Settled);
    }

    [Fact]
    public async Task A_pinned_agent_is_swept_without_listing_agents()
    {
        var (sweeper, http) = Create(pinnedAgent: "agent_ours");

        await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal([Settled, Active, Unfiltered], http.Requests.Select(r => r.Key));
    }

    [Fact]
    public async Task Interrupts_its_own_sessions_still_running_so_a_later_pass_can_delete_them()
    {
        // The vendor refuses to delete a running session, so a delete here would only fail.
        var (sweeper, http) = Create();
        http.Routes[Active] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_live", "running", "condux fix 9")}}]}""");
        http.Routes["POST /v1/sessions/sesn_live/events"] = (HttpStatusCode.OK, "{}");

        var (deleted, interrupted, _) = await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal((0, 1), (deleted, interrupted));
        var interrupt = Assert.Single(http.Requests, r => r.Key == "POST /v1/sessions/sesn_live/events");
        Assert.Contains("user.interrupt", interrupt.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(http.Requests, r => r.Key.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_backlog_of_settled_sessions_does_not_crowd_out_a_running_one()
    {
        // Each kind has its own page budget, so a running session, which spends until it is stopped, is
        // still reached when the settled ones fill every page this pass may read.
        var (sweeper, http) = Create();
        http.Routes[Settled] = (HttpStatusCode.OK, """{"data":[],"next_page":"again"}""");
        http.Routes[Settled + "&page=again"] = (HttpStatusCode.OK, """{"data":[],"next_page":"again"}""");
        http.Routes[Active] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_live", "running", "condux fix 9")}}]}""");
        http.Routes["POST /v1/sessions/sesn_live/events"] = (HttpStatusCode.OK, "{}");

        var (_, interrupted, _) = await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal(1, interrupted);
    }

    [Fact]
    public async Task Interrupts_a_session_in_a_status_this_version_does_not_know()
    {
        // It may be a session still working, and only an interrupt can stop one. The pass over every status
        // touches only the unknown ones: the known ones belong to the passes filtered to them.
        var (sweeper, http) = Create();
        http.Routes[Unfiltered] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_odd", "a_status_this_version_does_not_know", "condux fix 3")}},{{Session("sesn_live", "running", "condux fix 4")}},{{Session("sesn_done", "idle", "condux fix 5")}}]}""");
        http.Routes["POST /v1/sessions/sesn_odd/events"] = (HttpStatusCode.OK, "{}");
        http.Routes["DELETE /v1/sessions/sesn_odd"] = (HttpStatusCode.OK, "{}");

        var (deleted, interrupted, _) = await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal((0, 1), (deleted, interrupted));
        Assert.Contains(http.Requests, r => r.Key == "POST /v1/sessions/sesn_odd/events");
        // Then deleted, in case the status means finished: interrupting alone would keep its sandbox.
        Assert.Contains(http.Requests, r => r.Key == "DELETE /v1/sessions/sesn_odd");
        Assert.DoesNotContain(http.Requests, r => r.Key.Contains("sesn_live", StringComparison.Ordinal));
        Assert.DoesNotContain(http.Requests, r => r.Key.Contains("sesn_done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_finished_session_in_an_unknown_status_is_deleted_even_when_the_interrupt_is_refused()
    {
        var (sweeper, http) = Create();
        http.Routes[Unfiltered] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_odd", "a_finished_status_this_version_does_not_know", "condux fix 3")}}]}""");
        http.Routes["POST /v1/sessions/sesn_odd/events"] =
            (HttpStatusCode.BadRequest, """{"error":{"message":"session is not running"}}""");
        http.Routes["DELETE /v1/sessions/sesn_odd"] = (HttpStatusCode.OK, "{}");

        await sweeper.SweepAsync(Cutoff, default);

        Assert.Contains(http.Requests, r => r.Key == "DELETE /v1/sessions/sesn_odd");
    }

    [Fact]
    public async Task An_interrupt_that_fails_to_connect_does_not_skip_the_delete()
    {
        var (sweeper, http) = Create();
        http.Routes[Unfiltered] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_odd", "a_status_this_version_does_not_know", "condux fix 3")}}]}""");
        http.Failures["POST /v1/sessions/sesn_odd/events"] = new HttpRequestException("connection reset");
        http.Routes["DELETE /v1/sessions/sesn_odd"] = (HttpStatusCode.OK, "{}");

        var result = await sweeper.SweepAsync(Cutoff, default);

        Assert.Contains(http.Requests, r => r.Key == "DELETE /v1/sessions/sesn_odd");
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task An_agent_listing_that_fails_comes_back_as_the_result_not_as_a_throw()
    {
        var (sweeper, http) = Create();
        http.Routes[Agents] = (HttpStatusCode.InternalServerError, """{"error":{"message":"down"}}""");

        var result = await sweeper.SweepAsync(Cutoff, default);

        Assert.IsType<ManagedAgentsApiException>(result.Failure);
        Assert.Equal((0, 0), (result.Deleted, result.Stopped));
    }

    [Fact]
    public async Task A_listing_that_fails_ends_only_its_own_pass_and_is_reported_after_the_rest()
    {
        // One failed list call used to end the whole sweep, so the passes after it went undone that tick.
        var (sweeper, http) = Create();
        http.Routes[Settled] = (HttpStatusCode.InternalServerError, """{"error":{"message":"down"}}""");
        http.Routes[Active] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_live", "running", "condux fix 9")}}]}""");
        http.Routes["POST /v1/sessions/sesn_live/events"] = (HttpStatusCode.OK, "{}");

        var result = await sweeper.SweepAsync(Cutoff, default);

        // Reported with the work that did succeed, so neither hides the other.
        Assert.IsType<ManagedAgentsApiException>(result.Failure);
        Assert.Equal(1, result.Stopped);
        Assert.Contains(http.Requests, r => r.Key == "POST /v1/sessions/sesn_live/events");
        Assert.Contains(http.Requests, r => r.Key == Unfiltered);
    }

    [Fact]
    public async Task Follows_the_next_page_cursor()
    {
        var (sweeper, http) = Create();
        http.Routes[Settled] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_a", "idle", "condux fix 1")}}],"next_page":"p2"}""");
        http.Routes[Settled + "&page=p2"] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_b", "idle", "condux fix 2")}}],"next_page":null}""");
        http.Routes["DELETE /v1/sessions/sesn_a"] = (HttpStatusCode.OK, "{}");
        http.Routes["DELETE /v1/sessions/sesn_b"] = (HttpStatusCode.OK, "{}");

        var (deleted, _, _) = await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task A_cursor_that_never_ends_stops_at_the_page_bound()
    {
        var (sweeper, http) = Create();
        http.Routes[Settled] = (HttpStatusCode.OK, """{"data":[],"next_page":"again"}""");
        http.Routes[Settled + "&page=again"] = (HttpStatusCode.OK, """{"data":[],"next_page":"again"}""");

        await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal(10, http.Requests.Count(r => r.Key.StartsWith(Settled, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task One_session_the_vendor_refuses_does_not_stop_the_pass()
    {
        // Say it started running again between the list and the delete; the next pass meets it again.
        var (sweeper, http) = Create();
        http.Routes[Settled] = (HttpStatusCode.OK,
            $$"""{"data":[{{Session("sesn_busy", "idle", "condux fix 1")}},{{Session("sesn_free", "idle", "condux fix 2")}}]}""");
        http.Routes["DELETE /v1/sessions/sesn_busy"] =
            (HttpStatusCode.BadRequest, """{"error":{"message":"cannot delete/archive while running"}}""");
        http.Routes["DELETE /v1/sessions/sesn_free"] = (HttpStatusCode.OK, "{}");

        var (deleted, _, _) = await sweeper.SweepAsync(Cutoff, default);

        Assert.Equal(1, deleted);
        Assert.Contains(http.Requests, r => r.Key == "DELETE /v1/sessions/sesn_free");
    }

    [Fact]
    public async Task The_cutoff_is_written_the_same_under_any_culture()
    {
        // A Thai culture counts years from 543 BC, so a culture-dependent cutoff named 2569 and matched every
        // session the gateway has, including runs still being polled.
        var (sweeper, http) = Create();
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            await sweeper.SweepAsync(Cutoff, default);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Contains(http.Requests, r => r.Key == Settled);
    }
}
