using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class EmailAllowlistTests
{
    [Fact]
    public void Blank_or_null_config_allows_no_one()
    {
        Assert.Equal(0, new EmailAllowlist(null).Count);
        Assert.Equal(0, new EmailAllowlist("").Count);
        Assert.False(new EmailAllowlist(null).Contains("anyone@condux.dev"));
    }

    [Fact]
    public void Parses_csv_and_matches_case_insensitively_with_trimming()
    {
        var allow = new EmailAllowlist("  Admin@Condux.dev , ops@condux.dev ");

        Assert.Equal(2, allow.Count);
        Assert.True(allow.Contains("admin@condux.dev"));
        Assert.True(allow.Contains("ADMIN@CONDUX.DEV"));
        Assert.True(allow.Contains("  ops@condux.dev  "));
        Assert.False(allow.Contains("intruder@condux.dev"));
    }

    [Fact]
    public void Ignores_empty_and_whitespace_entries()
    {
        Assert.Equal(2, new EmailAllowlist("a@b.co,,, ,c@d.co").Count);
    }
}
