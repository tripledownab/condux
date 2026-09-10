using Condux.Agent.Sandbox;
using Xunit;

namespace Condux.Conductor.Tests;

public class SandboxTarTests
{
    [Fact]
    public void A_packed_file_round_trips_through_the_archive()
    {
        var tar = SandboxTar.FromFiles(new Dictionary<string, string>
        {
            ["src/cart.ts"] = "export const items = [];",
        });

        Assert.Equal("export const items = [];", SandboxTar.FirstFileContents(tar));
    }

    /// <summary>
    /// An entry name is what Docker turns into a path when it expands the archive, so a traversing one
    /// writes outside the workspace directory. Both callers pack through here: the workspace seeds the
    /// checkout with it and stages every agent write with it, so guarding one caller would have left the
    /// other open.
    /// </summary>
    [Theory]
    [InlineData("../escape.ts")]
    [InlineData("src/../../escape.ts")]
    [InlineData("/etc/passwd")]
    [InlineData(@"..\escape.ts")]
    public void A_traversing_entry_name_is_refused_rather_than_packed(string path) =>
        Assert.Throws<ArgumentException>(
            () => SandboxTar.FromFiles(new Dictionary<string, string> { [path] = "x" }));

    /// <summary>A leading "./" is normalized away rather than packed as a literal dot directory.</summary>
    [Fact]
    public void A_relative_prefix_is_normalized_off_the_entry_name()
    {
        var tar = SandboxTar.FromFiles(new Dictionary<string, string> { ["./src/cart.ts"] = "x" });

        Assert.Equal("x", SandboxTar.FirstFileContents(tar));
    }

    [Fact]
    public void Contents_survive_non_ascii_intact()
    {
        // Source files carry accents, symbols and emoji in strings and comments; a mangled round trip would
        // corrupt the file the agent then rewrites.
        const string source = "const message = \"héllo wörld — ✅\";";

        var tar = SandboxTar.FromFiles(new Dictionary<string, string> { ["src/i18n.ts"] = source });

        Assert.Equal(source, SandboxTar.FirstFileContents(tar));
    }

    [Fact]
    public void An_archive_with_no_regular_file_reads_as_nothing()
    {
        Assert.Null(SandboxTar.FirstFileContents(SandboxTar.FromFiles(new Dictionary<string, string>())));
    }
}
