using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>The pure object-key deriver (ADR-0028): debugId keys ahead of release+path, keys are stable
/// for re-upload idempotence, and a filename cannot inject extra key hierarchy.</summary>
public sealed class SourceMapKeysTests
{
    [Fact]
    public void DebugId_keys_the_artifact_regardless_of_release_or_filename()
    {
        var a = SourceMapKeys.ForArtifact(7, "1.0.0", "web", "AbC-123", "app.js");
        var b = SourceMapKeys.ForArtifact(7, "2.0.0", null, "AbC-123", "other.js");
        Assert.Equal("sourcemaps/7/by-debug-id/AbC-123", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Without_debugId_keys_on_release_dist_filename()
    {
        Assert.Equal(
            "sourcemaps/7/by-release/1.0.0/web/app.min.js",
            SourceMapKeys.ForArtifact(7, "1.0.0", "web", null, "app.min.js"));
    }

    [Fact]
    public void Missing_dist_uses_a_placeholder_segment()
    {
        Assert.Equal(
            "sourcemaps/7/by-release/1.0.0/_/app.js",
            SourceMapKeys.ForArtifact(7, "1.0.0", null, null, "app.js"));
    }

    [Fact]
    public void A_filename_cannot_inject_extra_key_hierarchy()
    {
        var key = SourceMapKeys.ForArtifact(7, "1.0.0", null, null, "../../etc/passwd");
        // The filename's path separators collapse to '_', so the key keeps exactly its intended depth.
        Assert.StartsWith("sourcemaps/7/by-release/1.0.0/_/", key);
        Assert.Equal(6, key.Split('/').Length);
    }

    [Fact]
    public void Is_deterministic_for_the_same_inputs()
    {
        Assert.Equal(
            SourceMapKeys.ForArtifact(1, "v1", "d", "id", "f.js"),
            SourceMapKeys.ForArtifact(1, "v1", "d", "id", "f.js"));
    }
}
