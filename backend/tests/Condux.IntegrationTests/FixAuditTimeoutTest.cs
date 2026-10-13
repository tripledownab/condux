using Condux.Core.FixEngine;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The audit write is best-effort: every caller has already committed what the entry describes, so a
/// failure must not fail the request. An HttpClient or driver timeout arrives as a cancellation nobody
/// asked for, and it used to escape as if the caller had given up. The caller's own cancellation still
/// escapes, since that one is a request ending rather than a write failing. No database: the store is a
/// stub whose only working member is the audit append.
/// </summary>
public class FixAuditTimeoutTest
{
    [Fact]
    public async Task A_timed_out_audit_write_is_swallowed()
    {
        var store = new ThrowingAuditStore(new TaskCanceledException("timed out"));

        await FixAudit.TryWriteAsync(
            store, NullLogger.Instance, Guid.NewGuid(), "1", "cancelled", new { }, CancellationToken.None);

        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_escapes()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var store = new ThrowingAuditStore(new OperationCanceledException(aborted.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FixAudit.TryWriteAsync(
            store, NullLogger.Instance, Guid.NewGuid(), "1", "cancelled", new { }, aborted.Token));
    }

    private sealed class ThrowingAuditStore(Exception failure) : IFixStore
    {
        public int Calls { get; private set; }

        public Task AppendAuditAsync(
            Guid fixId, string actor, string eventName, string detailJson, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw failure;
        }

        public Task<bool> InsertAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryQueueAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryCancelQueuedAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> TryClaimAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> MarkRunningAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryFailAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordSuccessAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FixSuggestion?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FixSuggestion>> ListByIssueAsync(
            long issueId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
