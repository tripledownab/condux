using Condux.Core.Alerting;
using Xunit;

namespace Condux.Core.Tests;

public class AlertTemplateTests
{
    private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>(
        StringComparer.Ordinal)
    {
        ["title"] = "TypeError: boom",
        ["level"] = "Error",
        ["culprit"] = "app.py",
    };

    [Fact]
    public void Render_substitutes_known_tokens()
    {
        Assert.Equal("Error: TypeError: boom @ app.py",
            AlertTemplate.Render("{{level}}: {{title}} @ {{culprit}}", Values));
    }

    [Fact]
    public void Render_tolerates_whitespace_in_braces()
    {
        Assert.Equal("TypeError: boom", AlertTemplate.Render("{{ title }}", Values));
    }

    [Fact]
    public void Render_is_single_pass_so_a_value_cannot_inject_another_token()
    {
        // A title that itself looks like a token must render literally, not re-expand into the level.
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = "{{level}}",
            ["level"] = "Fatal",
        };
        Assert.Equal("{{level}}", AlertTemplate.Render("{{title}}", values));
    }

    [Fact]
    public void Render_unknown_token_becomes_empty()
    {
        Assert.Equal("x  y", AlertTemplate.Render("x {{nope}} y", Values));
    }

    [Fact]
    public void UnknownTokens_flags_only_the_unknown_ones()
    {
        Assert.Equal(new[] { "nope" }, AlertTemplate.UnknownTokens("{{title}} {{nope}} {{level}}"));
        Assert.Empty(AlertTemplate.UnknownTokens(AlertTemplate.Default));
    }
}
