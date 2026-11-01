using Condux.ControlPlane.Setup;
using Condux.Core.Events;
using Condux.Core.Issues;
using Condux.Core.Tickets;
using Condux.Storage.Postgres;
using System.Globalization;
using Condux.Storage.ClickHouse;
using Condux.Storage.SourceMaps;
using Condux.Telemetry;

namespace Condux.ControlPlane.Tickets;

/// <summary>Lets a request that reserved a ticket wake the worker on this replica now, rather than at its
/// next tick. A wake that lands on no worker is harmless: the tick finds the row anyway.</summary>
internal sealed class TicketWorkerSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another request woke it first, which is all this one wanted.
            }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

/// <summary>
/// The one caller of the tracker (ADR-0053 decision 5). A click and an alert only reserve a row; this
/// claims pending rows, builds each ticket from its issue, asks the sink for it and records the answer, then
/// nudges the project so the issue rail updates. Being the only caller is what makes the reservation hold:
/// if a request could call Linear while this could also claim its row, the two would open two tickets.
/// </summary>
internal sealed class TicketWorker(
    IssueTicketRepository tickets, ProjectTrackerMappingRepository mappings, ITrackerConnectionStore connections,
    IssueRepository issues, ITicketSink sink, ProjectEventNotifier projectEvents, TicketWorkerSignal signal,
    IServiceScopeFactory scopes, IConfiguration cfg, ILogger<TicketWorker> logger, ConduxSelfReporter selfReport)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // Longer than the sink's worst case (two refreshes, two creates and the lookup after a refusal, at the
    // client timeout, plus the event read's budget), so a claim older than this belongs to a worker that
    // stopped, not to one still waiting on Linear.
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(2);

    // The event read is best effort, so it gets a short budget of its own: the reader's resilience handler
    // retries an unreachable store for longer than that, and inside the claim timeout every second spent
    // there is one Linear's calls do not have.
    private static readonly TimeSpan EventReadBudget = TimeSpan.FromSeconds(5);

    // An automatic ticket whose first event is not in the event store yet waits this long and tries again,
    // until it is MaxEventWait old, then goes without. The consumer flushes a batch when it fills or the
    // topic goes quiet, with no flush by age, so under steady traffic the settle delay is not always enough.
    private static readonly TimeSpan EventWaitStep = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxEventWait = TimeSpan.FromMinutes(2);

    // How long a row waits after Linear asked us to slow down or could not be reached.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    // A pass stops here and the rest waits a tick, so one pass cannot hold the loop for ever.
    private const int MaxPerPass = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await signal.WaitAsync(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ticket worker pass failed");
                selfReport.Report(ex); // Condux on Condux (#75)
                // Wait out the interval, or a failure that repeats (the database is down) spins the loop.
                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>One pass: move stale claims to uncertain, then work through pending rows.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var projectId in (await tickets.MarkStaleUncertainAsync(ClaimTimeout, ct)).Distinct())
        {
            await ProjectEventNudge.TrySendAsync(projectEvents, logger, projectId, ct);
        }

        // A retry later defers the row, and the claim then passes over every row of that org until it is
        // due (IssueTicketRepository.ClaimNextAsync), so other orgs' rows still go this pass.
        for (var i = 0; i < MaxPerPass && await tickets.ClaimNextAsync(ct) is { } claimed; i++)
        {
            if (await SendAsync(claimed, ct) is not { } outcome)
            {
                await tickets.PutBackAsync(claimed.Id, EventWaitStep, ct);
                continue;
            }
            if (!await tickets.RecordAsync(claimed.Id, outcome, RetryDelay, ct))
            {
                logger.LogWarning("ticket answer arrived after its claim expired ticket={TicketId}", claimed.Id);
            }
            await ProjectEventNudge.TrySendAsync(projectEvents, logger, claimed.ProjectId, ct);
        }
    }

    /// <summary>What sending the claimed ticket established, or null to put it back unsent: an automatic
    /// ticket whose event has not reached the event store, while it is young enough to wait for it.</summary>
    private async Task<TicketOutcome?> SendAsync(ClaimedTicket claimed, CancellationToken ct)
    {
        // The row was reserved against a workspace. If the org has since disconnected or moved to another
        // workspace, the ticket has nowhere to go that the person who asked for it chose.
        if (await connections.GetAsync(claimed.OrgId, claimed.Provider, ct) is not { } connection
            || connection.WorkspaceId != claimed.WorkspaceId)
        {
            return TicketOutcome.Fail(TicketFailure.NotConnected);
        }
        if (await mappings.GetAsync(claimed.ProjectId, claimed.Provider, ct) is not { } mapping)
        {
            return TicketOutcome.Fail(TicketFailure.NotMapped);
        }
        var summaries = await issues.SummariesByInternalIdsAsync([claimed.IssueId], ct);
        if (!summaries.TryGetValue(claimed.IssueId, out var issue))
        {
            // The issue deletes its tickets, so this is a delete racing the claim.
            return TicketOutcome.Fail(TicketFailure.Refused);
        }

        var sample = await ReadLatestEventAsync(claimed, ct);
        if (sample is null && claimed.Automatic && claimed.Age < MaxEventWait)
        {
            return null;
        }
        var content = TicketText.ToContent(new TicketSource(
            issue.Title, issue.Culprit, (Level)issue.Level, issue.FirstRelease,
            IssueLinks.ToIssueUrl(AppUrls.BaseUrl(cfg), issue.Id),
            issue.FirstSeen, sample?.Environment, TicketText.ToFrames(sample)));
        var outcome = await sink.CreateAsync(
            claimed.OrgId, mapping.TargetId, [.. mapping.Labels.Select(l => l.Id)], claimed.Id, content, ct);
        // A refusal the project can act on flags its tab: a team the app lost, or, when the project puts
        // labels on its tickets, a refusal Linear gave no reason for, since a label deleted or withdrawn on
        // Linear's side is the likely one and nothing else here would surface it.
        if (outcome.Failure == TicketFailure.TeamUnavailable
            || (outcome.Failure == TicketFailure.Refused && mapping.Labels.Count > 0))
        {
            await mappings.MarkNeedsAttentionAsync(
                claimed.ProjectId, claimed.Provider, mapping.TargetId, [.. mapping.Labels.Select(l => l.Id)], ct);
        }
        return outcome;
    }

    /// <summary>
    /// The issue's newest stored event, de-minified the way the issue page shows it, for the ticket's
    /// environment and stack excerpt. Best effort: a ticket without them still points at the issue, so an
    /// event store that does not answer costs those two lines, logged, rather than the ticket. The reader
    /// is a typed HTTP client, resolved per call rather than held, since this worker lives for the process.
    /// </summary>
    private async Task<Event?> ReadLatestEventAsync(ClaimedTicket claimed, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(EventReadBudget);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var events = scope.ServiceProvider.GetRequiredService<ClickHouseEventReader>();
            var symbolication = scope.ServiceProvider.GetRequiredService<EventSymbolication>();
            var latest = await events.RecentByIssueAsync(
                claimed.ProjectId.ToString(CultureInfo.InvariantCulture), (ulong)claimed.IssueId, limit: 1, budget.Token);
            var shown = await symbolication.SymbolicateAsync(claimed.ProjectId, latest, budget.Token);
            if (shown.Count == 0)
            {
                logger.LogInformation("ticket built before its issue's first event was stored ticket={TicketId}", claimed.Id);
                return null;
            }
            return shown[0].ToEvent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ticket built without its latest event ticket={TicketId}", claimed.Id);
            return null;
        }
    }
}
