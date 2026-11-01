using Condux.Core.Repos;
using Xunit;

namespace Condux.Core.Tests;

public class ReleaseRecordsTests
{
    [Theory]
    [InlineData("abc1234")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void A_commit_id_of_seven_to_sixty_four_hex_characters_is_one(string commit) =>
        Assert.True(ReleaseRecords.IsCommitId(commit));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("abc1234 ")]
    [InlineData("v1.4.0x")]
    [InlineData("main")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void Anything_else_is_not_a_commit_id(string? commit) =>
        Assert.False(ReleaseRecords.IsCommitId(commit));

    [Fact]
    public void A_release_is_recorded_trimmed()
    {
        var (release, error) = ReleaseRecords.ToRecordable(" 1.4.0 ", "\tabc1234\n");

        Assert.Null(error);
        Assert.Equal(("1.4.0", "abc1234"), (release!.Version, release.Commit));
    }

    [Theory]
    [InlineData(null, "abc1234", ReleaseRecords.InvalidVersion)]
    [InlineData("  ", "abc1234", ReleaseRecords.InvalidVersion)]
    [InlineData("1.4.0", null, ReleaseRecords.InvalidCommit)]
    [InlineData("1.4.0", "  ", ReleaseRecords.InvalidCommit)]
    [InlineData("1.4.0", "v1.4.0", ReleaseRecords.InvalidCommit)]
    public void A_blank_version_or_a_commit_that_is_not_an_id_is_refused(string? version, string? commit, string expected)
    {
        var (release, error) = ReleaseRecords.ToRecordable(version, commit);

        Assert.Null(release);
        Assert.Equal(expected, error);
    }
}
