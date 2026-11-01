using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Http;
using Condux.Core.Tickets;

namespace Condux.Linear;

/// <summary>The Linear workspace a grant belongs to.</summary>
public sealed record LinearWorkspace(string Id, string Name);

/// <summary>A team the app can create issues in.</summary>
public sealed record LinearTeam(string Id, string Key, string Name);

/// <summary>A label that can go on an issue in a team: one of the team's own or a workspace label, with the
/// group it sits in, if any.</summary>
public sealed record LinearLabel(string Id, string Name, string? GroupId, string? GroupName);

/// <summary>The labels for a team, and whether that is all of them: the list stops at a page ceiling, and a
/// label past it is not one Linear stopped offering.</summary>
public sealed record LinearLabelList(IReadOnlyList<LinearLabel> Labels, bool Complete);

/// <summary>A create that was refused because the access token was, which a refresh may cure.</summary>
public sealed class LinearUnauthorizedException() : Exception("Linear refused the access token.");

/// <summary>
/// The GraphQL operations Condux needs (the workspace, teams, labels, creating an issue and looking one up
/// by id), against the published schema
/// (github.com/linear/linear, packages/sdk/src/schema.graphql, read 2026-10-20). Errors are classified the
/// way Linear's own SDK does in packages/sdk/src/error.ts: by the first error's <c>extensions.type</c>,
/// then by HTTP status. A 200 can still carry errors, so the errors array is always read.
/// </summary>
public sealed class LinearApi(HttpClient http, LinearOptions options)
{
    private const int MaxTeamPages = 10;

    /// <summary>The workspace the token belongs to. Throws <see cref="LinearUnauthorizedException"/> on a
    /// refused token, <see cref="HttpRequestException"/> on anything else that is not an answer.</summary>
    public async Task<LinearWorkspace> GetWorkspaceAsync(string accessToken, CancellationToken ct = default)
    {
        var data = await QueryAsync(accessToken, "query { organization { id name } }", null, ct);
        return ReadShaped(() =>
        {
            var org = data.GetProperty("organization");
            return new LinearWorkspace(RequireString(org, "id"), RequireString(org, "name"));
        });
    }

    /// <summary>Every team the app can see, which Linear's admins decide. Paged, with a ceiling so a
    /// misbehaving cursor cannot loop.</summary>
    public async Task<IReadOnlyList<LinearTeam>> ListTeamsAsync(string accessToken, CancellationToken ct = default)
    {
        var teams = new List<LinearTeam>();
        string? after = null;
        for (var page = 0; page < MaxTeamPages; page++)
        {
            var data = await QueryAsync(
                accessToken,
                "query Teams($after: String) { teams(first: 100, after: $after) "
                + "{ nodes { id key name } pageInfo { hasNextPage endCursor } } }",
                new Dictionary<string, object?> { ["after"] = after }, ct);
            var (nodes, hasNext, cursor) = ReadShaped(() =>
            {
                var connection = data.GetProperty("teams");
                var info = connection.GetProperty("pageInfo");
                return (connection.GetProperty("nodes").EnumerateArray().Select(t => new LinearTeam(
                        RequireString(t, "id"), RequireString(t, "key"), RequireString(t, "name"))).ToList(),
                    info.GetProperty("hasNextPage").GetBoolean(), info.GetProperty("endCursor").GetString());
            });
            teams.AddRange(nodes);
            if (!hasNext)
            {
                break;
            }
            after = cursor;
        }
        return teams;
    }

    /// <summary>
    /// The labels an issue in <paramref name="teamId"/> can carry: the workspace's and that team's, without
    /// group labels, which Linear does not let an issue carry directly. Paged with the same ceiling as teams.
    /// </summary>
    public async Task<LinearLabelList> ListLabelsAsync(string accessToken, string teamId, CancellationToken ct = default)
    {
        var labels = new List<LinearLabel>();
        string? after = null;
        var complete = false;
        for (var page = 0; page < MaxTeamPages && !complete; page++)
        {
            var data = await QueryAsync(
                accessToken,
                "query Labels($after: String, $team: ID) { issueLabels(first: 100, after: $after, filter: "
                + "{ isGroup: { eq: false }, or: [{ team: { null: true } }, { team: { id: { eq: $team } } }] }) "
                + "{ nodes { id name parent { id name } } pageInfo { hasNextPage endCursor } } }",
                new Dictionary<string, object?> { ["after"] = after, ["team"] = teamId }, ct);
            var (nodes, hasNext, cursor) = ReadShaped(() =>
            {
                var connection = data.GetProperty("issueLabels");
                var info = connection.GetProperty("pageInfo");
                return (connection.GetProperty("nodes").EnumerateArray().Select(ToLabel).ToList(),
                    info.GetProperty("hasNextPage").GetBoolean(), info.GetProperty("endCursor").GetString());
            });
            labels.AddRange(nodes);
            complete = !hasNext;
            after = cursor;
        }
        // A label can arrive on two pages if the list changes while it is read, so the first copy wins.
        return new LinearLabelList([.. labels.DistinctBy(l => l.Id)], complete);
    }

    private static LinearLabel ToLabel(JsonElement label)
    {
        var grouped = label.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object;
        return new LinearLabel(
            RequireString(label, "id"), RequireString(label, "name"),
            grouped ? RequireString(parent, "id") : null, grouped ? RequireString(parent, "name") : null);
    }

    /// <summary>
    /// Create an issue with our own id. Linear refuses a create that repeats an id it already holds
    /// (measured against a real workspace, 2026-10-20), so a refusal is followed by a lookup under the same
    /// id: an issue that is there is the one an earlier attempt made, and it is recorded rather than
    /// reported as refused. Throws <see cref="LinearUnauthorizedException"/> when the token was refused,
    /// which the caller answers by refreshing; every other answer is an outcome.
    /// </summary>
    public async Task<TicketOutcome> CreateIssueAsync(
        string accessToken, string teamId, IReadOnlyList<string> labelIds, Guid id, TicketContent content,
        CancellationToken ct = default)
    {
        var outcome = await SendCreateAsync(accessToken, teamId, labelIds, id, content, ct);
        return outcome.Failure == TicketFailure.Refused ? await FindExistingAsync(accessToken, id, outcome, ct) : outcome;
    }

    // The lookup after a refusal. Found means created; Linear answering that no such issue exists means the
    // refusal stands; anything else leaves it open whether one exists, which is uncertain.
    private async Task<TicketOutcome> FindExistingAsync(string accessToken, Guid id, TicketOutcome refused, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(accessToken, "query Issue($id: String!) { issue(id: $id) { identifier url } }",
                new Dictionary<string, object?> { ["id"] = id.ToString() }, ct);
        }
        catch (Exception e) when (LinearHttp.IsCallFailure(e, ct))
        {
            return TicketOutcome.Uncertain;
        }
        using (response)
        {
            var body = await TryReadAsync(response, ct);
            if (ReadErrorType(body) is { } type)
            {
                // Read as "no such issue", so the refusal stands. Linear answers an id it does not hold with
                // HTTP 200 and the type "invalid input" (measured against a real workspace, 2026-10-21), and
                // "user error" is read the same way; any other type leaves it open whether one exists, which
                // errs toward asking a person.
                return type is "invalid input" or "user error" ? refused : TicketOutcome.Uncertain;
            }
            return response.IsSuccessStatusCode && ReadIssue(body, "issue") is { } existing
                ? TicketOutcome.Success(existing)
                : TicketOutcome.Uncertain;
        }
    }

    private async Task<TicketOutcome> SendCreateAsync(
        string accessToken, string teamId, IReadOnlyList<string> labelIds, Guid id, TicketContent content,
        CancellationToken ct)
    {
        var input = new Dictionary<string, object?>
        {
            ["id"] = id.ToString(),
            ["teamId"] = teamId,
            ["title"] = content.Title,
            ["description"] = content.Body,
        };
        // Left out rather than sent empty or null, so a project with no labels sends exactly the request it
        // sent before labels existed.
        if (labelIds.Count > 0)
        {
            input["labelIds"] = labelIds;
        }
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(
                accessToken,
                "mutation Create($input: IssueCreateInput!) { issueCreate(input: $input) "
                + "{ success issue { identifier url } } }",
                new Dictionary<string, object?> { ["input"] = input },
                ct);
        }
        catch (HttpRequestException e) when (WasNeverSent(e))
        {
            return TicketOutcome.RetryLater;
        }
        catch (Exception e) when (LinearHttp.IsCallFailure(e, ct))
        {
            // The request may have reached Linear: a reset connection or a timeout says nothing either way.
            return TicketOutcome.Uncertain;
        }

        using (response)
        {
            var body = await TryReadAsync(response, ct);
            var type = ReadErrorType(body);
            if (type is null && response.IsSuccessStatusCode && ReadCreated(body) is { } created)
            {
                return TicketOutcome.Success(created);
            }
            return ToOutcome(type ?? ToStatusType(response.StatusCode));
        }
    }

    /// <summary>
    /// The created issue, or null when the answer does not say one was made. The URL is checked here
    /// because the dashboard renders it as a link: anything but an absolute http(s) URL, a
    /// <c>javascript:</c> one included, is not a ticket we can show, so the attempt reads as uncertain.
    /// </summary>
    private static TicketRef? ReadCreated(JsonElement? body) =>
        body is { } ok
        && ok.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("issueCreate", out var payload) && payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
            ? ToTicketRef(payload, "issue")
            : null;

    private static TicketRef? ReadIssue(JsonElement? body, string field) =>
        body is { } ok && ok.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? ToTicketRef(data, field)
            : null;

    private static TicketRef? ToTicketRef(JsonElement parent, string field) =>
        parent.TryGetProperty(field, out var issue) && issue.ValueKind == JsonValueKind.Object
        && issue.TryGetProperty("identifier", out var key) && key.ValueKind == JsonValueKind.String
        && issue.TryGetProperty("url", out var url) && HttpUrls.IsAbsoluteHttp(url.GetString())
            ? new TicketRef(key.GetString()!, url.GetString()!)
            : null;

    /// <summary>How a classified error reads as a ticket outcome. Only the types that say the request was
    /// handled and refused are failures; the rest may or may not have created the issue.</summary>
    private static TicketOutcome ToOutcome(string? type) => type switch
    {
        "authentication error" => throw new LinearUnauthorizedException(),
        "forbidden" => TicketOutcome.Fail(TicketFailure.TeamUnavailable),
        "ratelimited" => TicketOutcome.RetryLater,
        "invalid input" or "user error" or "feature not accessible" or "usage limit exceeded" or "graphql error"
            => TicketOutcome.Fail(TicketFailure.Refused),
        _ => TicketOutcome.Uncertain,
    };

    // Linear's SDK reads the status only when the body carried no typed error, in this order.
    private static string? ToStatusType(HttpStatusCode status) => (int)status switch
    {
        403 => "forbidden",
        429 => "ratelimited",
        >= 400 and < 500 => "authentication error",
        _ => null,
    };

    private static string? ReadErrorType(JsonElement? body) =>
        body is { } b && b.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
        && errors.GetArrayLength() > 0
            ? errors[0].TryGetProperty("extensions", out var ext) && ext.TryGetProperty("type", out var t)
                ? t.GetString()?.ToLowerInvariant()
                : "unknown"
            : null;

    // Failures that happen before a request leaves this machine.
    private static bool WasNeverSent(HttpRequestException e) => e.HttpRequestError
        is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
        or HttpRequestError.SecureConnectionError;

    // An answer missing a field it should carry is an answer we cannot use, which callers already handle as
    // a failed call. Left alone it would surface as a KeyNotFoundException and a 500.
    private static T ReadShaped<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            throw new HttpRequestException("Linear answered in a shape this client does not read.", e);
        }
    }

    // GetString() answers a JSON null with a null rather than throwing, so a field Linear sent as null would
    // otherwise pass as a value and fail later, somewhere less clear.
    private static string RequireString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new KeyNotFoundException(name);

    private async Task<JsonElement> QueryAsync(
        string accessToken, string query, Dictionary<string, object?>? variables, CancellationToken ct)
    {
        using var response = await SendAsync(accessToken, query, variables, ct);
        var body = await TryReadAsync(response, ct);
        var type = ReadErrorType(body) ?? ToStatusType(response.StatusCode);
        if (type == "authentication error")
        {
            throw new LinearUnauthorizedException();
        }
        if (type is not null || !response.IsSuccessStatusCode || body is not { } ok
            || !ok.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new HttpRequestException($"Linear answered {(int)response.StatusCode} ({type ?? "no data"}).");
        }
        return data;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string accessToken, string query, Dictionary<string, object?>? variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBaseUrl}/graphql")
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request, ct);
    }

    private static async Task<JsonElement?> TryReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
