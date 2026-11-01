using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every third party image the deployment runs names a release, never a floating tag. A host resolves a
/// floating tag once, at its first pull: the deploy runs <c>up -d --build</c> and never pulls, so
/// <c>postgres:17-alpine</c> kept production on a minor release that later ones had patched, and nothing
/// noticed. When a pinned tag changes in the file, the host lacks it and the next deploy pulls it, and
/// Dependabot proposes each bump. A plain file check, so it runs in the unit pass, which ci.yml runs for
/// a change to any of these files.
/// </summary>
public sealed class ImagePinsTest
{
    // How many numeric parts a release of each image has. Postgres numbers a release major.minor (17.11);
    // the rest major.minor.patch or more (24.8.14.39), so for them two parts (24.8) still floats.
    private static readonly Dictionary<string, int> ReleaseParts = new() { ["postgres"] = 2 };
    private const int DefaultReleaseParts = 3;

    [Fact]
    public void Every_compose_image_names_a_release()
    {
        var deploy = Path.Combine(RepoFiles.FindRoot(), "deploy");
        var images = Directory.EnumerateFiles(deploy, "docker-compose*.yml")
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (File: Path.GetFileName(path), Number: index + 1, Text: line.Trim())))
            .Where(line => line.Text.StartsWith("image: ", StringComparison.Ordinal))
            .ToList();

        var floating = images
            .Where(line => !IsRelease(line.Text["image: ".Length..].Trim()))
            .Select(line => $"{line.File}:{line.Number} {line.Text}")
            .ToList();

        // A scan that read nothing passes too, so prove it found the stack's own images.
        Assert.True(images.Count >= 5, $"read only {images.Count} image lines");
        Assert.True(floating.Count == 0, $"pin a release: {string.Join(", ", floating)}");
    }

    [Fact]
    public void Each_image_the_stack_runs_has_one_release()
    {
        // The postgres and clickhouse images each appear twice (the service and its migration job),
        // and a fixture reads one of them, so two releases would leave a test running the other one.
        // Every repository the file names, so an image added later is covered without editing this.
        var repositories = File.ReadLines(Path.Combine(RepoFiles.FindRoot(), "deploy", "docker-compose.yml"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("image: ", StringComparison.Ordinal))
            .Select(line => line["image: ".Length..].Trim())
            .Select(image => image[..image.LastIndexOf(':')])
            .Distinct()
            .ToList();

        Assert.True(repositories.Count >= 5, $"read only {repositories.Count} repositories");
        foreach (var repository in repositories)
        {
            ComposeImages.For(repository);
        }
    }

    [Fact]
    public void The_migrations_image_runs_the_clickhouse_the_stack_runs()
    {
        var dockerfile = File.ReadAllLines(Path.Combine(RepoFiles.FindRoot(), "deploy", "migrations.Dockerfile"));

        Assert.Contains($"FROM {ComposeImages.For("clickhouse/clickhouse-server")}", dockerfile);
    }

    [Theory]
    [InlineData("postgres:17-alpine", false)]
    [InlineData("postgres:17.11-alpine", true)]
    [InlineData("clickhouse/clickhouse-server:24.8", false)]
    [InlineData("clickhouse/clickhouse-server:24.8.14.39", true)]
    [InlineData("caddy:2-alpine", false)]
    [InlineData("axllent/mailpit:latest", false)]
    [InlineData("redpandadata/redpanda:v24.2.7", true)]
    [InlineData("ghcr.io/rustfs/rustfs:1.0.0", true)]
    [InlineData("postgres@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    public void A_release_is_told_from_a_floating_tag(string image, bool release)
    {
        Assert.Equal(release, IsRelease(image));
    }

    private static bool IsRelease(string image)
    {
        // A digest names one exact image, which is a stronger pin than any tag.
        if (image.Contains("@sha256:", StringComparison.Ordinal))
        {
            return true;
        }
        var colon = image.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }
        var repository = image[..colon];
        var parts = ReleaseParts.GetValueOrDefault(repository, DefaultReleaseParts);
        return Regex.IsMatch(image[(colon + 1)..], $@"^v?\d+(\.\d+){{{parts - 1},}}(-[\w.]+)?$");
    }
}
