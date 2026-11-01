using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// No integration test checks a response by calling <c>EnsureSuccessStatusCode</c> or by asserting
/// <c>IsSuccessStatusCode</c>, both of which drop the body. They go through
/// <see cref="HttpResponses.EnsureSuccessAsync"/>, which keeps it in the failure message. This covers only
/// those two spellings: <c>GetFromJsonAsync</c> and its siblings check the status inside the framework, so
/// a test reading through them loses the body of any failed answer. An exception in a host built with
/// <see cref="ServerExceptions"/> reaches it either way.
/// </summary>
public class HttpResponsesScanTest
{
    // Built from parts, so the scan does not find its own patterns.
    private static readonly Regex Dropped = new(
        @"\bEnsureSuccess" + @"StatusCode\b|Assert\.True\([^;]*\.IsSuccess" + @"StatusCode\)",
        RegexOptions.Compiled);

    [Fact]
    public void No_integration_test_drops_a_failed_response_body()
    {
        var root = Path.Combine(RepoFiles.FindRoot(), "backend", "tests", "Condux.IntegrationTests");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Where(path => !path.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        // Code lines only: the helper's own documentation names what it replaces.
        var offenders = files
            .SelectMany(path => File.ReadAllLines(Path.Combine(root, path)).Select((line, i) => (path, line, i)))
            .Where(hit => !hit.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && Dropped.IsMatch(hit.line))
            .Select(hit => $"{hit.path}:{hit.i + 1}")
            .ToList();

        Assert.Contains(Path.Combine("Fixtures", "ApiAuth.cs"), files);
        Assert.True(offenders.Count == 0,
            "use EnsureSuccessAsync or EnsureStatusAsync, which keep the body, at: " + string.Join(", ", offenders));
    }
}
