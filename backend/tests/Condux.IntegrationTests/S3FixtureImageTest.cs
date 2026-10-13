using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The integration fixture and the dev compose stack run the same S3-compatible image. Both are pinned
/// to a release, and a bump in one alone would test one server while developers run another. A plain
/// file check rather than an integration test, so it runs in the unit pass on every change.
/// </summary>
public sealed class S3FixtureImageTest
{
    [Fact]
    public void The_dev_compose_object_store_runs_the_image_the_fixture_tests()
    {
        var compose = File.ReadAllLines(Path.Combine(RepoFiles.FindRoot(), "deploy", "docker-compose.yml"));

        Assert.Contains(compose, line => line.Trim() == $"image: {S3Fixture.Image}");
    }
}
