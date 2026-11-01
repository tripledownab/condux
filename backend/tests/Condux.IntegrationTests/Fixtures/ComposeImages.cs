namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// The third party images the stack runs, read from <c>deploy/docker-compose.yml</c> so a test container
/// starts exactly the image the deployment starts. Production layers its overlay on that file, so a bump
/// there reaches every fixture with no second place to edit, and no fixture can test a version the stack
/// does not run.
/// </summary>
public static class ComposeImages
{
    private static readonly Lazy<string[]> ImageLines = new(() =>
        File.ReadAllLines(Path.Combine(RepoFiles.FindRoot(), "deploy", "docker-compose.yml"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("image: ", StringComparison.Ordinal))
            .Select(line => line["image: ".Length..].Trim())
            .ToArray());

    /// <summary>The one image the compose file names for <paramref name="repository"/>, tag included.</summary>
    public static string For(string repository)
    {
        var images = ImageLines.Value
            .Where(image => image.StartsWith(repository + ":", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        return images.Length == 1
            ? images[0]
            : throw new InvalidOperationException(
                $"deploy/docker-compose.yml names {images.Length} images for {repository}, expected one");
    }
}
