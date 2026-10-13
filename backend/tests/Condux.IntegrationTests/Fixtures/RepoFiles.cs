namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Locates files in the repository from a running test, for tests that check a committed file against
/// the code (a source scan, or a config value that has to match a fixture). The test runs from its bin
/// directory, so this walks up to the first ancestor holding <c>backend</c>.
/// </summary>
public static class RepoFiles
{
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // Every line of every C# file under backend/src, skipping build output, for a scan that fails on a
    // shape. File is relative to backend/src and Number counts from 1.
    public static IReadOnlyList<SourceLine> ReadSourceLines()
    {
        var source = Path.Combine(FindRoot(), "backend", "src");
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        return Directory
            .EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(obj, StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path)
                .Select((text, index) => new SourceLine(Path.GetRelativePath(source, path), index + 1, text)))
            .ToList();
    }
}

public sealed record SourceLine(string File, int Number, string Text);
