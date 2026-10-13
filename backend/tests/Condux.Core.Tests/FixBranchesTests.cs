using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class FixBranchesTests
{
    [Fact]
    public void Two_runs_on_one_issue_started_together_get_different_branches()
    {
        // Run ids are UUIDv7, whose leading characters are a timestamp, so a branch built from a prefix of
        // the id repeats for runs started in the same minute and the second run's branch already exists.
        var first = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(20_000));
        var second = Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(20_000));

        Assert.NotEqual(FixBranches.ToBranch("42", first), FixBranches.ToBranch("42", second));
    }

    [Fact]
    public void The_branch_names_what_was_fixed_and_carries_the_whole_run_id()
    {
        var runId = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

        Assert.Equal(
            "condux/fix-GHSA-jf85-cpcp-j695-0199a1b2c3d47e5f8a9b0c1d2e3f4a5b",
            FixBranches.ToBranch("GHSA-jf85-cpcp-j695", runId));
    }
}
