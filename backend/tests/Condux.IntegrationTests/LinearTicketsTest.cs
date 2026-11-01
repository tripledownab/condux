extern alias consumer;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Secrets;
using consumer::Condux.Consumer;
using Condux.Core.Tickets;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Linear tickets end to end over the control plane and a stubbed Linear (ADR-0053): connecting by OAuth,
/// a project choosing its team, an issue reserving a ticket and the worker making it, and the guards that
/// keep one issue to one ticket however many clicks, tabs and retries arrive.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LinearTicketsTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string StateCookie = "condux_linear_state";

    private sealed record Setup(
        WebApplicationFactory<Program> App, HttpClient Client, LinearStub Linear, long OrgId, long ProjectId, Guid IssueId,
        string Email)
    {
        public string TicketPath => $"/api/projects/{ProjectId}/issues/{IssueId}/ticket";
    }

    private async Task<Setup> ProvisionAsync(bool connect = true, bool map = true)
    {
        // The worker claims the oldest pending row in the database, and this class's database outlives each
        // test, so a row an earlier test left pending would be claimed here and sent to this test's stub.
        // Tests in a class run one at a time, so anything still unfinished belongs to one that has ended.
        await using (var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString))
        {
            await conn.OpenAsync();
            await new Npgsql.NpgsqlCommand(
                $"DELETE FROM issue_tickets WHERE state IN ({(short)TicketState.Pending}, {(short)TicketState.Claimed});",
                conn).ExecuteNonQueryAsync();
        }
        var linear = new LinearStub();
        var app = linear.CreateApp(pg.ConnectionString);
        var client = app.CreateClient();
        var user = await ApiAuth.SignUpAsync(client);
        var org = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme" });
        var orgId = (await org.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var project = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects", new { name = "Backend", platform = "python" });
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetInt64();
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-" + Guid.NewGuid().ToString("N"), "TypeError: [boom](https://evil.test)", "app.py in pay"),
            Level.Error, DateTimeOffset.UtcNow);
        var setup = new Setup(app, client, linear, orgId, projectId, issue.PublicId, user.Email);
        if (connect)
        {
            Assert.Equal("connected", await CallbackOutcomeAsync(setup, setup.OrgId, withCookie: true));
        }
        if (map)
        {
            var put = await client.PutAsJsonAsync($"/api/projects/{projectId}/linear/mapping", new { teamId = "team-1" });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }
        return setup;
    }

    private static async Task<string?> CallbackOutcomeAsync(Setup s, long orgId, bool withCookie, string code = "the-code")
    {
        var state = ConnectState.Create(ConnectState.Linear, orgId, $"/projects/{s.ProjectId}", false,
            DateTimeOffset.UtcNow.AddMinutes(10), LinearStub.ClientSecret);
        var browser = s.App.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/linear/oauth/callback?code={code}&state={Uri.EscapeDataString(state)}");
        if (withCookie)
        {
            request.Headers.Add("Cookie", $"{StateCookie}={state}");
        }
        var response = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return HttpUtility.ParseQueryString(response.Headers.Location!.Query)["linear"];
    }

    private static async Task<JsonElement> TicketAsync(Setup s) => await s.Client.GetFromJsonAsync<JsonElement>(s.TicketPath);

    [Fact]
    public async Task Connect_asks_linear_for_the_narrow_scopes_as_the_app_and_remembers_the_state_in_a_cookie()
    {
        var s = await ProvisionAsync(connect: false, map: false);

        var resp = await s.Client.PostAsJsonAsync($"/api/orgs/{s.OrgId}/linear/connect", new { returnPath = "/projects/x" });

        var url = new Uri((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("read,issues:create", query["scope"]);
        Assert.Equal("app", query["actor"]);
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{StateCookie}={query["state"]};", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_callback_stores_the_grant_sealed_and_reports_the_workspace()
    {
        var s = await ProvisionAsync(map: false);

        var stored = await new TrackerConnectionRepository(pg.ConnectionString).GetAsync(s.OrgId, TicketProvider.Linear);
        Assert.Equal("ws-1", stored!.WorkspaceId);
        // Sealed: the bytes at rest are not the token, and the app's own box opens them.
        var secrets = s.App.Services.GetRequiredService<SecretBox>();
        var plain = secrets.Open(stored.RefreshTokenSealed);
        Assert.StartsWith("rt-", plain);
        Assert.Equal(-1, stored.RefreshTokenSealed.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plain)));

        var status = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/linear");
        Assert.True(status.GetProperty("connected").GetBoolean());
        Assert.Equal("Acme", status.GetProperty("workspaceName").GetString());
    }

    [Fact]
    public async Task A_callback_without_the_browsers_cookie_connects_nothing()
    {
        var s = await ProvisionAsync(connect: false, map: false);

        Assert.Equal("failed", await CallbackOutcomeAsync(s, s.OrgId, withCookie: false));
        Assert.Null(await new TrackerConnectionRepository(pg.ConnectionString).GetAsync(s.OrgId, TicketProvider.Linear));
    }

    // The exchange succeeded, so a grant exists at Linear, and then the workspace read gave nothing usable.
    // Nothing stores that grant, so the callback has to revoke it on the way out.
    [Fact]
    public async Task A_grant_the_callback_could_not_store_is_revoked_at_linear()
    {
        var s = await ProvisionAsync(connect: false, map: false);
        s.Linear.WorkspaceId = null;

        Assert.Equal("failed", await CallbackOutcomeAsync(s, s.OrgId, withCookie: true));

        Assert.Null(await new TrackerConnectionRepository(pg.ConnectionString).GetAsync(s.OrgId, TicketProvider.Linear));
        Assert.Contains(s.Linear.Requests, r => r.Path == "/oauth/revoke" && r.Body.Contains("token_type_hint=refresh_token"));
        Assert.Contains(s.Linear.Requests, r => r.Path == "/oauth/revoke" && r.Body.Contains("token_type_hint=access_token"));
    }

    [Fact]
    public async Task Reconnecting_to_a_different_workspace_clears_every_team_choice()
    {
        var s = await ProvisionAsync();
        var mappings = new ProjectTrackerMappingRepository(pg.ConnectionString);

        Assert.Equal("connected", await CallbackOutcomeAsync(s, s.OrgId, withCookie: true));
        Assert.NotNull(await mappings.GetAsync(s.ProjectId, TicketProvider.Linear));

        s.Linear.WorkspaceId = "ws-2";
        Assert.Equal("connected", await CallbackOutcomeAsync(s, s.OrgId, withCookie: true));
        Assert.Null(await mappings.GetAsync(s.ProjectId, TicketProvider.Linear));
    }

    [Fact]
    public async Task A_project_can_only_choose_a_team_linear_says_the_app_can_reach()
    {
        var s = await ProvisionAsync(map: false);

        var unknown = await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping", new { teamId = "team-elsewhere" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var teams = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/linear/teams");
        Assert.Equal("ENG", teams[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Creating_a_ticket_without_a_team_or_a_connection_is_refused_with_a_reason()
    {
        var unmapped = await ProvisionAsync(map: false);
        var resp = await unmapped.Client.PostAsync(unmapped.TicketPath, null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("linear_not_mapped", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var unconnected = await ProvisionAsync(connect: false, map: false);
        resp = await unconnected.Client.PostAsync(unconnected.TicketPath, null);
        Assert.Equal("linear_not_connected", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_click_reserves_and_the_worker_creates_the_ticket_under_the_rows_own_id()
    {
        var s = await ProvisionAsync();

        var reserve = await s.Client.PostAsync(s.TicketPath, null);
        Assert.Equal(HttpStatusCode.Accepted, reserve.StatusCode);
        var pending = await reserve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)TicketState.Pending, pending.GetProperty("state").GetInt32());
        Assert.Equal(0, s.Linear.Creates); // the request never calls Linear itself

        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        var ticket = await TicketAsync(s);
        var id = pending.GetProperty("id").GetString()!;
        Assert.Equal((int)TicketState.Created, ticket.GetProperty("state").GetInt32());
        Assert.Equal($"ENG-{id[..4]}", ticket.GetProperty("key").GetString());
        Assert.False(ticket.GetProperty("automatic").GetBoolean()); // a person asked for this one
        var sent = s.Linear.Requests.Single(r => r.Body.Contains("issueCreate")).Body;
        Assert.Contains($"\"id\":\"{id}\"", sent);
        // The title travels as written, and the culprit is escaped in the body.
        Assert.Contains("\"title\":\"TypeError: [boom](https://evil.test)\"", sent);
        Assert.Contains("app\\\\.py in pay", sent);
    }

    // The guarantee the whole reservation exists for: however many requests arrive, one row, one ticket.
    [Fact]
    public async Task Concurrent_clicks_reserve_one_row_and_make_one_ticket()
    {
        var s = await ProvisionAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => s.Client.PostAsync(s.TicketPath, null)));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()));
        Assert.Single(ids.Distinct());

        var worker = LinearStub.WorkerOf(s.App);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => worker.RunOnceAsync(CancellationToken.None)));
        Assert.Equal(1, s.Linear.Creates);
    }

    [Fact]
    public async Task A_claim_left_too_long_becomes_uncertain_and_is_never_claimed_again_on_its_own()
    {
        var s = await ProvisionAsync();
        await s.Client.PostAsync(s.TicketPath, null);
        var tickets = new IssueTicketRepository(pg.ConnectionString);
        Assert.NotNull(await tickets.ClaimNextAsync()); // a worker that then stopped
        await BackdateClaimsAsync(TimeSpan.FromMinutes(3));

        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        Assert.Equal((int)TicketState.Uncertain, (await TicketAsync(s)).GetProperty("state").GetInt32());
        Assert.Equal(0, s.Linear.Creates);
        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, s.Linear.Creates);
    }

    [Fact]
    public async Task A_retry_resends_under_the_same_id()
    {
        var s = await ProvisionAsync();
        s.Linear.CreateBody = """{"errors":[{"message":"boom","extensions":{"type":"internal error"}}]}""";
        var first = await (await s.Client.PostAsync(s.TicketPath, null)).Content.ReadFromJsonAsync<JsonElement>();
        var worker = LinearStub.WorkerOf(s.App);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal((int)TicketState.Uncertain, (await TicketAsync(s)).GetProperty("state").GetInt32());

        s.Linear.CreateBody = null;
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.PostAsync($"{s.TicketPath}/retry", null)).StatusCode);
        await worker.RunOnceAsync(CancellationToken.None);

        var id = first.GetProperty("id").GetString()!;
        Assert.Equal((int)TicketState.Created, (await TicketAsync(s)).GetProperty("state").GetInt32());
        Assert.All(s.Linear.Requests.Where(r => r.Body.Contains("issueCreate")), r => Assert.Contains(id, r.Body));
    }

    [Fact]
    public async Task A_team_linear_refuses_fails_the_ticket_and_flags_the_project()
    {
        var s = await ProvisionAsync();
        s.Linear.CreateBody = """{"errors":[{"message":"no","extensions":{"type":"forbidden"}}]}""";
        await s.Client.PostAsync(s.TicketPath, null);

        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        var ticket = await TicketAsync(s);
        Assert.Equal((int)TicketState.Failed, ticket.GetProperty("state").GetInt32());
        Assert.Equal((int)TicketFailure.TeamUnavailable, ticket.GetProperty("failure").GetInt32());
        var status = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/linear");
        Assert.True(status.GetProperty("mapping").GetProperty("needsAttention").GetBoolean());
    }

    [Fact]
    public async Task Unlinking_lets_the_issue_have_a_new_ticket()
    {
        var s = await ProvisionAsync();
        var first = (await (await s.Client.PostAsync(s.TicketPath, null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync(s.TicketPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.GetAsync(s.TicketPath)).StatusCode);
        var second = (await (await s.Client.PostAsync(s.TicketPath, null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Disconnecting_revokes_at_linear_and_removes_the_grant_even_when_the_revoke_fails()
    {
        var s = await ProvisionAsync();
        s.Linear.RevokeStatus = HttpStatusCode.InternalServerError;

        var resp = await s.Client.DeleteAsync($"/api/orgs/{s.OrgId}/linear");

        Assert.False((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetBoolean());
        Assert.Contains(s.Linear.Requests, r => r.Path == "/oauth/revoke" && r.Body.Contains("token_type_hint=refresh_token"));
        Assert.Null(await new TrackerConnectionRepository(pg.ConnectionString).GetAsync(s.OrgId, TicketProvider.Linear));
        // The dialog said these projects lose their team, so they do.
        Assert.Null(await new ProjectTrackerMappingRepository(pg.ConnectionString).GetAsync(s.ProjectId, TicketProvider.Linear));
    }

    [Fact]
    public async Task A_ticket_reserved_before_a_disconnect_fails_rather_than_going_somewhere_else()
    {
        var s = await ProvisionAsync();
        await s.Client.PostAsync(s.TicketPath, null);
        var ticketId = (await TicketAsync(s)).GetProperty("id").GetGuid();
        await s.Client.DeleteAsync($"/api/orgs/{s.OrgId}/linear");

        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, s.Linear.Creates);
        var issueInternal = (await new IssueRepository(pg.ConnectionString).GetByPublicIdAsync(s.ProjectId, s.IssueId))!.Value.InternalId;
        var row = await new IssueTicketRepository(pg.ConnectionString).GetAsync(issueInternal, TicketProvider.Linear, "ws-1");
        Assert.Equal(ticketId, row!.Id);
        Assert.Equal(TicketState.Failed, row.State);
        Assert.Equal(TicketFailure.NotConnected, row.Failure);
    }

    // Forces the overlap rather than hoping for it: a first worker holds its claim open, a second claims
    // while it does, and the first then commits. However the second waited, it must not take the same row.
    [Fact]
    public async Task A_worker_claiming_while_another_holds_the_row_does_not_take_it_too()
    {
        var s = await ProvisionAsync();
        await s.Client.PostAsync(s.TicketPath, null);
        var tickets = new IssueTicketRepository(pg.ConnectionString);

        await using var first = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await first.OpenAsync();
        await using var tx = await first.BeginTransactionAsync();
        await using (var claim = new Npgsql.NpgsqlCommand(
            $"UPDATE issue_tickets SET state = {(short)TicketState.Claimed}, claimed_at = now() WHERE state = {(short)TicketState.Pending};",
            first, tx))
        {
            Assert.Equal(1, await claim.ExecuteNonQueryAsync());
        }

        var second = tickets.ClaimNextAsync();
        await WaitUntilDoneOrBlockedAsync(second);
        await tx.CommitAsync();

        Assert.Null(await second);
    }

    // Either the claim returned (it skipped the locked row) or it is waiting on that lock. Both mean it has
    // read the row, which is the point the first worker's commit has to land after.
    private async Task WaitUntilDoneOrBlockedAsync(Task claim)
    {
        await using var watcher = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await watcher.OpenAsync();
        var database = new Npgsql.NpgsqlConnectionStringBuilder(pg.ConnectionString).Database;
        for (var i = 0; i < 200 && !claim.IsCompleted; i++)
        {
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND wait_event_type = 'Lock';", watcher);
            cmd.Parameters.AddWithValue("db", database!);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(claim.IsCompleted, "the second claim neither finished nor waited on the lock");
    }

    // Past the settle delay, and, since this class runs without an event store, past the longest an
    // automatic ticket waits for its event, so the worker sends it without one.
    private async Task SettleAutomaticTicketsAsync(bool pastEventWait = true)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var age = pastEventWait ? ", created_at = now() - interval '3 minutes'" : "";
        await new Npgsql.NpgsqlCommand(
            $"UPDATE issue_tickets SET not_before = now() - interval '1 second'{age} WHERE automatic;", conn)
            .ExecuteNonQueryAsync();
    }

    // The consumer flushes events by batch, not by age, so the settle delay is not always enough: a young
    // automatic ticket with no event yet goes back to wait, rather than out without its environment.
    [Fact]
    public async Task A_young_automatic_ticket_waits_for_its_event_and_goes_without_once_it_is_old()
    {
        var s = await ProvisionAsync();
        await TurnOnAutoCreateAsync(s);
        await Dispatcher().DispatchAsync(s.ProjectId, await NewIssueAsync(s, Level.Error), Level.Error);
        var worker = LinearStub.WorkerOf(s.App);

        await SettleAutomaticTicketsAsync(pastEventWait: false);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, s.Linear.Creates);

        await SettleAutomaticTicketsAsync();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, s.Linear.Creates);
    }

    // Claims are stamped and swept on the database's clock, so a test ages one there rather than passing
    // the worker a time of its own.
    private async Task BackdateClaimsAsync(TimeSpan age)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"UPDATE issue_tickets SET claimed_at = now() - @age WHERE state = {(short)TicketState.Claimed};", conn);
        cmd.Parameters.AddWithValue("age", age);
        await cmd.ExecuteNonQueryAsync();
    }

    // Rate limits and grants are per org, so one org's 429 must not hold up another org's ticket, and the
    // row Linear deferred must wait rather than be claimed again on the very next pass.
    [Fact]
    public async Task One_org_being_rate_limited_neither_blocks_another_nor_spins_on_its_own_row()
    {
        var limited = await ProvisionAsync();
        var fine = await ProvisionAsync();
        // The limited org has a backlog, and every one of its tickets is limited. One worker and one stub
        // carry both orgs' rows, so the stub's limit is what tells them apart.
        var issues = new IssueRepository(pg.ConnectionString);
        foreach (var n in Enumerable.Range(0, 3))
        {
            var more = await issues.UpsertAsync(limited.ProjectId,
                new Grouping($"fp-more-{n}-{Guid.NewGuid():N}", $"Error {n}", "app.py"), Level.Error, DateTimeOffset.UtcNow);
            var reserved = await limited.Client.PostAsync($"/api/projects/{limited.ProjectId}/issues/{more.PublicId}/ticket", null);
            limited.Linear.RateLimitedIds.Add((await reserved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }
        var first = await limited.Client.PostAsync(limited.TicketPath, null);
        limited.Linear.RateLimitedIds.Add((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        await fine.Client.PostAsync(fine.TicketPath, null);
        var worker = LinearStub.WorkerOf(limited.App);

        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal((int)TicketState.Created, (await TicketAsync(fine)).GetProperty("state").GetInt32());
        // One call for the whole limited org across both passes: its first refusal deferred the org, not
        // just the row, so the rest of its backlog waited instead of spending a call each.
        Assert.Equal(1, limited.Linear.Requests.Count(r =>
            r.Body.Contains("issueCreate") && limited.Linear.RateLimitedIds.Any(r.Body.Contains)));
    }

    // A settling automatic ticket waits on its own clock. It must not pause the org's other tickets, or a
    // project that keeps having new errors would never get a click through.
    [Fact]
    public async Task A_settling_automatic_ticket_does_not_hold_up_a_click_in_the_same_org()
    {
        var s = await ProvisionAsync();
        await TurnOnAutoCreateAsync(s);
        await Dispatcher().DispatchAsync(s.ProjectId, await NewIssueAsync(s, Level.Error), Level.Error);
        await s.Client.PostAsync(s.TicketPath, null);

        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        Assert.Equal((int)TicketState.Created, (await TicketAsync(s)).GetProperty("state").GetInt32());
        Assert.Equal(1, s.Linear.Creates); // the click, while the automatic one is still settling
    }

    // An answer that arrives after the sweep told a person to check must not overwrite what they were told.
    [Fact]
    public async Task An_answer_that_lands_after_its_claim_expired_is_not_recorded()
    {
        var s = await ProvisionAsync();
        await s.Client.PostAsync(s.TicketPath, null);
        var tickets = new IssueTicketRepository(pg.ConnectionString);
        var claimed = await tickets.ClaimNextAsync();
        await BackdateClaimsAsync(TimeSpan.FromMinutes(3));
        await tickets.MarkStaleUncertainAsync(TimeSpan.FromMinutes(2));

        Assert.False(await tickets.RecordAsync(
            claimed!.Id, TicketOutcome.Success(new TicketRef("ENG-9", "https://x")), TimeSpan.Zero));
        Assert.Equal((int)TicketState.Uncertain, (await TicketAsync(s)).GetProperty("state").GetInt32());
    }

    [Fact]
    public async Task Renewing_or_breaking_a_grant_is_refused_when_the_stored_refresh_token_has_moved_on()
    {
        var s = await ProvisionAsync(map: false);
        var connections = new TrackerConnectionRepository(pg.ConnectionString);
        var read = (await connections.GetAsync(s.OrgId, TicketProvider.Linear))!;
        var secrets = s.App.Services.GetRequiredService<SecretBox>();

        // A second process renews first.
        Assert.True(await connections.ReplaceTokensAsync(s.OrgId, TicketProvider.Linear, read.RefreshTokenSealed,
            secrets.Seal("at-2"), secrets.Seal("rt-2"), DateTimeOffset.UtcNow.AddDays(1)));

        // The first, still holding what it read, can neither overwrite that renewal nor break the grant.
        Assert.False(await connections.ReplaceTokensAsync(s.OrgId, TicketProvider.Linear, read.RefreshTokenSealed,
            secrets.Seal("at-x"), secrets.Seal("rt-x"), DateTimeOffset.UtcNow.AddDays(1)));
        Assert.False(await connections.MarkBrokenAsync(s.OrgId, TicketProvider.Linear, read.RefreshTokenSealed));
        var now = (await connections.GetAsync(s.OrgId, TicketProvider.Linear))!;
        Assert.Equal("rt-2", secrets.Open(now.RefreshTokenSealed));
        Assert.Null(now.BrokenAt);
    }

    // Accepting an invite deletes a solo org, and the cascade would forget a grant that still works at Linear.
    [Fact]
    public async Task Abandoning_a_solo_org_for_an_invite_revokes_its_linear_grant_first()
    {
        var s = await ProvisionAsync(map: false);
        var inviter = s.App.CreateClient();
        await ApiAuth.SignUpAsync(inviter);
        var other = await inviter.PostAsJsonAsync("/api/orgs", new { name = "Other" });
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var invite = await inviter.PostAsJsonAsync($"/api/orgs/{otherId}/invites", new { email = s.Email, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var accept = await s.Client.PostAsJsonAsync("/api/invites/accept", new { token });

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Contains(s.Linear.Requests, r => r.Path == "/oauth/revoke" && r.Body.Contains("token_type_hint=refresh_token"));
        Assert.Null(await new TrackerConnectionRepository(pg.ConnectionString).GetAsync(s.OrgId, TicketProvider.Linear));
    }

    // Not a stranger with no org, which any filter would stop, but an admin of another org, which is the
    // case a route reading the wrong id would let through.
    [Fact]
    public async Task An_admin_of_another_org_reaches_none_of_this_orgs_linear_routes()
    {
        var s = await ProvisionAsync();
        var other = s.App.CreateClient();
        await ApiAuth.SignUpAsync(other);
        await other.PostAsJsonAsync("/api/orgs", new { name = "Other" });

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(s.TicketPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync(s.TicketPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{s.TicketPath}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(s.TicketPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/projects/{s.ProjectId}/linear")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/projects/{s.ProjectId}/linear/teams")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/projects/{s.ProjectId}/linear/labels")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping", new { teamId = "team-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/projects/{s.ProjectId}/linear/mapping")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/orgs/{s.OrgId}/linear")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsJsonAsync($"/api/orgs/{s.OrgId}/linear/connect", new { returnPath = "/" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/orgs/{s.OrgId}/linear")).StatusCode);
    }

    // Creating a ticket is triage, so a member may; choosing the team and the connection are an admin's.
    [Fact]
    public async Task A_member_can_create_a_ticket_but_not_change_the_team_or_the_connection()
    {
        var s = await ProvisionAsync();
        var member = s.App.CreateClient();
        var memberEmail = $"m-{Guid.NewGuid():N}@condux.test";
        var invite = await s.Client.PostAsJsonAsync($"/api/orgs/{s.OrgId}/invites", new { email = memberEmail, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        await ApiAuth.SignUpAsync(member, memberEmail);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync("/api/invites/accept", new { token })).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await member.PostAsync(s.TicketPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping", new { teamId = "team-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/projects/{s.ProjectId}/linear/mapping")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync($"/api/orgs/{s.OrgId}/linear/connect", new { returnPath = "/" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/orgs/{s.OrgId}/linear")).StatusCode);
    }

    // Labels come from Linear's live list for the mapped team, and only ids on that list can be saved.
    [Fact]
    public async Task A_project_puts_the_labels_it_chose_on_every_ticket_and_can_only_choose_real_ones()
    {
        var s = await ProvisionAsync();

        var labels = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/linear/labels");
        Assert.Equal(["Bug", "Backend"], labels.EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        var unknown = await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping",
            new { teamId = "team-1", labelIds = new[] { "label-elsewhere" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var saved = await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping",
            new { teamId = "team-1", labelIds = new[] { "label-bug" } });
        Assert.Equal("Bug", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("labels")[0].GetProperty("name").GetString());
        await s.Client.PostAsync(s.TicketPath, null);
        await LinearStub.WorkerOf(s.App).RunOnceAsync(CancellationToken.None);

        var input = JsonDocument.Parse(s.Linear.Requests.Single(r => r.Body.Contains("issueCreate")).Body)
            .RootElement.GetProperty("variables").GetProperty("input");
        Assert.Equal(["label-bug"], input.GetProperty("labelIds").EnumerateArray().Select(e => e.GetString()));
        // Built from the issue's own record, so it carries when the issue was first seen.
        Assert.Contains("**First seen:**", input.GetProperty("description").GetString());
    }

    // A label deleted in Linear must not lock the project's settings: a save drops it rather than refusing.
    [Fact]
    public async Task A_label_gone_from_linear_is_dropped_on_save_instead_of_refusing_every_later_save()
    {
        var s = await ProvisionAsync();
        await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping",
            new { teamId = "team-1", labelIds = new[] { "label-bug", "label-be" } });
        s.Linear.Labels = """[{"id":"label-be","name":"Backend","color":"#0f0"}]"""; // Bug deleted in Linear

        var save = await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping",
            new { teamId = "team-1", labelIds = new[] { "label-bug", "label-be" }, autoCreate = true });

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var labels = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("labels");
        Assert.Equal(["label-be"], labels.EnumerateArray().Select(l => l.GetProperty("id").GetString()));
        var bad = await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping",
            new { teamId = "team-1", labelIds = new string?[] { null } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private AutoTicketDispatcher Dispatcher() => new(
        new ProjectRepository(pg.ConnectionString), new ProjectTrackerMappingRepository(pg.ConnectionString),
        new TrackerConnectionRepository(pg.ConnectionString), new IssueTicketRepository(pg.ConnectionString),
        NullLogger<AutoTicketDispatcher>.Instance);

    private async Task TurnOnAutoCreateAsync(Setup s) =>
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectId}/linear/mapping",
            new { teamId = "team-1", autoCreate = true })).StatusCode);

    private async Task<UpsertResult> NewIssueAsync(Setup s, Level level) =>
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            s.ProjectId, new Grouping("fp-auto-" + Guid.NewGuid().ToString("N"), "Auto", "app.py"), level, DateTimeOffset.UtcNow);

    private async Task<long> TicketCountAsync(Setup s)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM issue_tickets t JOIN issues i ON i.id = t.issue_id WHERE i.project_id = @p AND t.automatic;", conn);
        cmd.Parameters.AddWithValue("p", s.ProjectId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // The checkbox: a new error reserves a ticket on its own, and nothing else does.
    [Fact]
    public async Task A_new_error_opens_a_ticket_on_its_own_only_when_the_project_turned_that_on()
    {
        var s = await ProvisionAsync();
        var dispatcher = Dispatcher();

        await dispatcher.DispatchAsync(s.ProjectId, await NewIssueAsync(s, Level.Error), Level.Error);
        Assert.Equal(0, await TicketCountAsync(s)); // off by default

        await TurnOnAutoCreateAsync(s);
        await dispatcher.DispatchAsync(s.ProjectId, await NewIssueAsync(s, Level.Warning), Level.Warning);
        var error = await NewIssueAsync(s, Level.Error);
        await dispatcher.DispatchAsync(s.ProjectId, error, Level.Error);
        await dispatcher.DispatchAsync(s.ProjectId, error with { Occurrence = 2 }, Level.Error);
        Assert.Equal(1, await TicketCountAsync(s)); // the new error only: not the warning, not its second event

        // It waits for its first event to reach the event store before it is sent.
        var worker = LinearStub.WorkerOf(s.App);
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, s.Linear.Creates);
        await SettleAutomaticTicketsAsync();
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, s.Linear.Creates);
        var status = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/linear");
        Assert.Equal(1, status.GetProperty("autoCreatedToday").GetInt32());
        // The rail says so: the setting opened it, not a person.
        var ticket = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/issues/{error.PublicId}/ticket");
        Assert.True(ticket.GetProperty("automatic").GetBoolean());
    }

    [Fact]
    public async Task Automatic_tickets_stop_at_the_daily_ceiling_and_an_already_ticketed_issue_spends_nothing()
    {
        var s = await ProvisionAsync();
        await TurnOnAutoCreateAsync(s);
        var tickets = new IssueTicketRepository(pg.ConnectionString);
        var first = await NewIssueAsync(s, Level.Error);
        var second = await NewIssueAsync(s, Level.Error);

        Assert.Equal(AutoReservation.Reserved, await tickets.ReserveAutomaticAsync(first.Id, s.ProjectId, TicketProvider.Linear, "ws-1", perDay: 1, TimeSpan.Zero));
        Assert.Equal(AutoReservation.AlreadyTicketed, await tickets.ReserveAutomaticAsync(first.Id, s.ProjectId, TicketProvider.Linear, "ws-1", perDay: 1, TimeSpan.Zero));
        Assert.Equal(AutoReservation.CeilingReached, await tickets.ReserveAutomaticAsync(second.Id, s.ProjectId, TicketProvider.Linear, "ws-1", perDay: 1, TimeSpan.Zero));
        Assert.Equal(1, await TicketCountAsync(s)); // the refused one was rolled back, not left pending
    }

    // A refusal that lands after an admin changed the labels is about the old choice, and must not flag the new.
    [Fact]
    public async Task A_late_refusal_does_not_flag_a_choice_an_admin_has_since_fixed()
    {
        var s = await ProvisionAsync();
        var mappings = new ProjectTrackerMappingRepository(pg.ConnectionString);
        await mappings.SetAsync(s.ProjectId, TicketProvider.Linear, "team-1", "Engineering", [new("label-be", "Backend")], false);

        await mappings.MarkNeedsAttentionAsync(s.ProjectId, TicketProvider.Linear, "team-1", ["label-bug"]);
        Assert.Null((await mappings.GetAsync(s.ProjectId, TicketProvider.Linear))!.NeedsAttentionAt);

        await mappings.MarkNeedsAttentionAsync(s.ProjectId, TicketProvider.Linear, "team-1", ["label-be"]);
        Assert.NotNull((await mappings.GetAsync(s.ProjectId, TicketProvider.Linear))!.NeedsAttentionAt);
    }

    // A list cut short at the page ceiling proves nothing about a saved label past it, so the picker still
    // gets it back rather than the dashboard marking it as gone from Linear.
    [Fact]
    public async Task A_saved_label_past_the_page_ceiling_is_still_offered()
    {
        var s = await ProvisionAsync();
        await new ProjectTrackerMappingRepository(pg.ConnectionString).SetAsync(
            s.ProjectId, TicketProvider.Linear, "team-1", "Engineering", [new("label-far", "Far away")], false);
        s.Linear.LabelsNeverEnd = true;

        var labels = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/linear/labels");

        Assert.Contains(labels.EnumerateArray(), l => l.GetProperty("id").GetString() == "label-far");
    }

    // On a list cut short a saved label is kept unchecked, so that only holds for the team it was saved
    // with: moving to another team, the old team's label is checked like any new one.
    [Fact]
    public async Task A_saved_label_past_the_page_ceiling_is_kept_on_save_for_its_own_team_only()
    {
        var s = await ProvisionAsync();
        await new ProjectTrackerMappingRepository(pg.ConnectionString).SetAsync(
            s.ProjectId, TicketProvider.Linear, "team-1", "Engineering", [new("label-far", "Far away")], false);
        s.Linear.Teams = """[{"id":"team-1","key":"ENG","name":"Engineering"},{"id":"team-2","key":"OPS","name":"Ops"}]""";
        s.Linear.LabelsNeverEnd = true;
        var path = $"/api/projects/{s.ProjectId}/linear/mapping";

        var sameTeam = await s.Client.PutAsJsonAsync(path, new { teamId = "team-1", labelIds = new[] { "label-far", "label-bug" } });
        var unknown = await s.Client.PutAsJsonAsync(path, new { teamId = "team-1", labelIds = new[] { "label-new" } });
        var otherTeam = await s.Client.PutAsJsonAsync(path, new { teamId = "team-2", labelIds = new[] { "label-far" } });

        Assert.Equal(HttpStatusCode.OK, sameTeam.StatusCode);
        var kept = (await sameTeam.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("labels").EnumerateArray()
            .Select(l => l.GetProperty("id").GetString()).ToArray();
        Assert.Equal(["label-far", "label-bug"], kept);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherTeam.StatusCode);
    }

    [Fact]
    public async Task A_stranger_sees_none_of_it()
    {
        var s = await ProvisionAsync();
        var outsider = s.App.CreateClient();
        await ApiAuth.SignUpAsync(outsider);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync(s.TicketPath, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/projects/{s.ProjectId}/linear")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/api/orgs/{s.OrgId}/linear")).StatusCode);
    }

}
