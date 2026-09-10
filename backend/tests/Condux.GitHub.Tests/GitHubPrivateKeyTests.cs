using System.Runtime.Versioning;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

/// <summary>
/// The GitHub App private key resolver, which two deployments share. The case worth pinning is a key
/// that exists but cannot be read: a host-mounted .pem owned by root is invisible to the non-root user
/// the containers run as, and it took the dashboard down once because the failure surfaced as a bare
/// UnauthorizedAccessException naming nothing.
///
/// <para>Unix-only, and annotated rather than skipped on Windows: making a file genuinely unreadable
/// needs file modes, and the deployments that mount a key are Linux containers. A host that cannot run
/// this should say so rather than report a pass.</para>
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class GitHubPrivateKeyTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "condux-key-" + Guid.NewGuid().ToString("N"));

    public GitHubPrivateKeyTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        // Restore the mode first, or the unreadable file defeats the cleanup on some filesystems.
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteKey(string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void An_inline_key_wins_over_a_path_that_would_also_resolve()
    {
        var path = WriteKey("from-disk.pem", "ON-DISK");

        Assert.Equal("INLINE", GitHubPrivateKey.Resolve("INLINE", path));
    }

    /// <summary>
    /// The content is a placeholder, and must stay one. <see cref="GitHubPrivateKey.Resolve"/> reads the
    /// file and never parses it, so a real PEM header would prove nothing this does not, while planting
    /// the opening line of a private key in a tree that publishes. The release scan reads every file it
    /// exports and cannot tell a fixture from the real thing, which is the right way round.
    /// </summary>
    [Fact]
    public void A_readable_key_file_is_read_verbatim_from_its_path()
    {
        var path = WriteKey("good.pem", "KEY-CONTENT\nsecond line\n");

        Assert.Equal("KEY-CONTENT\nsecond line\n", GitHubPrivateKey.Resolve(null, path));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "/nonexistent/condux/github.pem")]
    public void Absent_resolves_to_null_so_each_caller_decides_what_that_means(string? inline, string? path)
    {
        Assert.Null(GitHubPrivateKey.Resolve(inline, path));
    }

    /// <summary>
    /// The regression. This deliberately makes the file genuinely unreadable rather than faking the
    /// exception, because the claim being tested is what the runtime actually throws for a key mounted
    /// with the wrong owner, and a fake that throws on command could not falsify it.
    ///
    /// If this fails with "returned the key" the test process is running as root, where the mode does
    /// not apply and the scenario cannot be reproduced at all.
    /// </summary>
    [Fact]
    public void A_key_that_exists_but_cannot_be_read_says_so_and_names_the_fix()
    {
        var path = WriteKey("unreadable.pem", "SECRET");
        File.SetUnixFileMode(path, UnixFileMode.None);

        var failure = Record.Exception(() => GitHubPrivateKey.Resolve(null, path));

        Assert.NotNull(failure);
        var explained = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains(path, explained.Message, StringComparison.Ordinal);
        Assert.Contains("owned by the user the container runs as", explained.Message, StringComparison.Ordinal);
        // The cause is kept, so an operator reading logs still sees which syscall refused.
        Assert.NotNull(explained.InnerException);
    }
}
