using Condux.Agent;
using Xunit;
using Condux.Core.FixEngine;

namespace Condux.Conductor.Tests;

public class FixPlanParserTests
{
    private const string ValidJson =
        """{"summary":"Guard the null cart.","files":[{"path":"src/cart.js","contents":"fixed"}]}""";

    [Fact]
    public void Parses_a_bare_json_plan()
    {
        var plan = FixPlanParser.Parse(ValidJson);

        Assert.Equal("Guard the null cart.", plan.Summary);
        var file = Assert.Single(plan.Files);
        Assert.Equal("src/cart.js", file.Path);
        Assert.Equal("fixed", file.Contents);
    }

    [Fact]
    public void Parses_a_plan_wrapped_in_fences_or_prose()
    {
        Assert.Single(FixPlanParser.Parse($"```json\n{ValidJson}\n```").Files);
        Assert.Single(FixPlanParser.Parse($"Here is the fix:\n{ValidJson}\nLet me know!").Files);
    }

    [Fact]
    public void Missing_summary_becomes_empty_not_null()
    {
        var plan = FixPlanParser.Parse("""{"files":[{"path":"a.js","contents":"x"}]}""");
        Assert.Equal("", plan.Summary);
    }

    [Fact]
    public void Rejects_output_without_usable_changes()
    {
        Assert.Equal(FixFailureReason.NoUsableFix,
            Assert.Throws<FixFailedException>(() => FixPlanParser.Parse("no json here")).Reason);
        Assert.Equal(FixFailureReason.NoUsableFix,
            Assert.Throws<FixFailedException>(() => FixPlanParser.Parse("""{"summary":"s","files":[]}""")).Reason);
        Assert.Equal(FixFailureReason.NoUsableFix, Assert.Throws<FixFailedException>(
            () => FixPlanParser.Parse("""{"files":[{"path":"","contents":"x"}]}""")).Reason);
        Assert.Equal(FixFailureReason.NoUsableFix,
            Assert.Throws<FixFailedException>(() => FixPlanParser.Parse("{not valid json}")).Reason);
    }
}
