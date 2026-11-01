using System.Globalization;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.Scrub;
using Condux.Relay.Setup;

namespace Condux.Relay.Ingest;

/// <summary>What became of the events an OTLP export carried: how many were stored, and how many were
/// refused for each reason. The two reasons stay distinct because the sender's remedy differs.</summary>
internal readonly record struct OtlpPublished(long Admitted, long OverQuota, long Overflow)
{
    public long Rejected => OverQuota + Overflow;
}

/// <summary>The second half of an OTLP export, shared by the logs and traces endpoints: the per-request
/// ceiling, one quota call for the whole batch, scrub, publish, and the refund when a publish fails part
/// way. What differs between the endpoints is only how an export becomes events.</summary>
internal static class OtlpEventPublisher
{
    /// <param name="candidates">The events worth storing, already chosen by the caller. The list stops at
    /// <see cref="RelayOptions.MaxOtlpRecords"/>, because the body cap bounds the bytes of an export and not
    /// the records inside them; RelayOptions carries the arithmetic.</param>
    /// <param name="surplus">Candidates the caller found and did not build because they were past that
    /// ceiling, counted with the ones dropped here.</param>
    public static async Task<OtlpPublished> PublishAsync(
        HttpContext ctx, Project project, Limits limits, IReadOnlyList<Event> candidates,
        RelayOptions options, IQuotaMeter quotaMeter, IEventPublisher publisher, long surplus = 0)
    {
        var storable = candidates.Count > options.MaxOtlpRecords
            ? candidates.Take((int)options.MaxOtlpRecords).ToList()
            : candidates;
        long overflow = surplus + candidates.Count - storable.Count;

        long admitted = 0;
        if (storable.Count > 0)
        {
            // One quota call for the whole batch. The record count is the sender's to choose, so asking
            // per record made the amount of metering a stranger's to size, and against the Valkey meter
            // that is a network round trip each.
            var quota = await quotaMeter.TryConsumeAsync(
                project.QuotaKey, limits.MonthlyEvents, storable.Count, ctx.RequestAborted);
            if (limits.MonthlyEvents > 0)
            {
                ctx.Response.Headers["X-Condux-Quota-Remaining"] = quota.Remaining.ToString(CultureInfo.InvariantCulture);
            }
            admitted = quota.Admitted;
        }

        // A batch that straddles the quota spends what is left of it: the leading records are stored and
        // the rest ride back as partialSuccess, which is what the per-record loop did before.
        //
        // Quota is taken for the whole batch before the first publish, and taking it cannot be undone
        // by this request, so a publish that throws part way has spent the remainder on nothing. The
        // per-record loop could only ever lose one event that way; a batch can lose as many as it
        // took, which is what makes the refund worth a round trip on a path that is already failing.
        var published = 0;
        try
        {
            for (; published < storable.Count && published < admitted; published++)
            {
                var scrubbed = EventScrubber.Scrub(storable[published], project.UserKeySalt);
                // OTLP has no sent_at: an exporter reports no send time a drift could be read from.
                await publisher.PublishAsync(project.Id, scrubbed, limits.RetentionDays, 0, ctx.RequestAborted);
            }
        }
        catch
        {
            // Refund only what was admitted and not stored. An unlimited tier counted nothing, so it is
            // owed nothing. CancellationToken.None rather than ctx.RequestAborted: a disconnect is one
            // of the ways to get here, and passing the token that just fired would cancel the repair
            // for the failure that caused it. Nothing is swallowed, the exception carries on.
            var unused = admitted - published;
            if (limits.MonthlyEvents > 0 && unused > 0)
            {
                await quotaMeter.RefundAsync(project.QuotaKey, unused, CancellationToken.None);
            }
            throw;
        }

        return new OtlpPublished(admitted, storable.Count - admitted, overflow);
    }

    /// <summary>
    /// Says why events were refused, and what to do about it. Prose rather than a token because the
    /// OTLP export services define error_message as "a developer-facing human-readable message in
    /// English" that "should offer guidance on how users can address such issues"; the machine-readable
    /// part of a partial success is the rejected count. The two reasons stay distinct: a sender told its
    /// quota ran out when its batch was simply too large would go on paying for a plan that was never the
    /// problem. <paramref name="noun"/> names what the export is cut to and <paramref name="unit"/> what it is
    /// counted in, so the sentence reads true for logs and for spans alike.
    /// </summary>
    public static string RejectionReason(OtlpPublished result, string noun, string unit) =>
        (result.OverQuota, result.Overflow) switch
        {
            ( > 0, > 0) => $"the organization's monthly event quota is exhausted and the export carried more "
                + $"{noun} than one request accepts, so raise the plan's event limit and lower the "
                + "exporter's batch size",
            ( > 0, _) => "the organization's monthly event quota is exhausted, so raise the plan's event limit "
                + $"or export fewer {unit}",
            _ => $"the export carried more {noun} than one request accepts, so lower the exporter's "
                + "batch size",
        };
}
