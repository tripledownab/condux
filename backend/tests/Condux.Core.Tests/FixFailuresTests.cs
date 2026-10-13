using System.Net;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class FixFailuresTests
{
    [Fact]
    public void A_known_failure_keeps_the_reason_it_was_thrown_with()
    {
        Assert.Equal(FixFailureReason.NoSourceFiles,
            FixFailures.Classify(new FixFailedException(FixFailureReason.NoSourceFiles, "none")));
    }

    [Fact]
    public void A_timeout_reads_as_timed_out_whichever_way_it_arrives()
    {
        Assert.Equal(FixFailureReason.TimedOut, FixFailures.Classify(new TaskCanceledException()));
        Assert.Equal(FixFailureReason.TimedOut, FixFailures.Classify(new TimeoutException()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void GitHub_refusing_the_installation_or_repository_reads_as_source_host_access(HttpStatusCode status)
    {
        Assert.Equal(FixFailureReason.SourceHostAccess,
            FixFailures.Classify(new HttpRequestException("no", null, status)));
    }

    [Fact]
    public void Anything_else_is_unknown_rather_than_guessed()
    {
        Assert.Equal(FixFailureReason.Unknown, FixFailures.Classify(new InvalidOperationException("x")));
        Assert.Equal(FixFailureReason.Unknown,
            FixFailures.Classify(new HttpRequestException("no", null, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal(FixFailureReason.Unknown, FixFailures.Classify(new HttpRequestException("dns")));
    }

    [Theory]
    [InlineData(401, FixFailureReason.ModelKeyRejected)]
    [InlineData(403, FixFailureReason.ModelKeyRejected)]
    [InlineData(429, FixFailureReason.ModelUnavailable)]
    [InlineData(500, FixFailureReason.ModelUnavailable)]
    [InlineData(529, FixFailureReason.ModelUnavailable)]
    [InlineData(400, FixFailureReason.Unknown)]
    [InlineData(404, FixFailureReason.Unknown)]
    public void A_model_status_maps_to_the_reason_it_means(int status, FixFailureReason expected)
    {
        Assert.Equal(expected, FixFailures.ToModelReason((HttpStatusCode)status));
    }

    [Fact]
    public void A_failed_progress_carries_the_classified_reason()
    {
        var progress = AgentRunProgress.Failed(new FixFailedException(FixFailureReason.TimedOut, "slow"));

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Equal(FixFailureReason.TimedOut, progress.Reason);
        Assert.Equal("slow", progress.Error);
    }
}
