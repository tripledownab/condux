using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class FixAttentionTests
{
    [Theory]
    [InlineData(FixStatus.Pending, VerifyStatus.None, false)]
    [InlineData(FixStatus.Running, VerifyStatus.None, false)]
    [InlineData(FixStatus.Succeeded, VerifyStatus.None, true)]   // draft PR ready to review
    [InlineData(FixStatus.Succeeded, VerifyStatus.Watching, false)] // merged, verifying — in progress
    [InlineData(FixStatus.Succeeded, VerifyStatus.Held, true)]   // verification concluded
    [InlineData(FixStatus.Succeeded, VerifyStatus.DidNotHold, true)]
    [InlineData(FixStatus.Failed, VerifyStatus.None, true)]
    [InlineData(FixStatus.Cancelled, VerifyStatus.None, false)]
    public void NeedsAttention_matches_the_actionable_states(
        FixStatus status, VerifyStatus verify, bool expected) =>
        Assert.Equal(expected, FixAttention.NeedsAttention(status, verify));
}
