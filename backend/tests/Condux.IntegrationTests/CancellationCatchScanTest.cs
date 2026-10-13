using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Fails on a <c>catch (OperationCanceledException)</c>, or a <c>TaskCanceledException</c> one, that does
/// not say whose cancellation it means. HttpClient reports its own timeout as a
/// <see cref="TaskCanceledException"/>, so a bare catch reads a slow upstream as the host shutting down.
/// Most worker loops here had that shape, so a timeout that reached one stopped it for good, and the
/// runner's heartbeat ended the same way, letting a lease lapse under a run. A scan, because those sites
/// were found only by reading every one of them. It checks that a filter exists, not that it tests the
/// right token. The inverse shape, a catch-all that excludes every cancellation, has its own test below.
/// </summary>
public class CancellationCatchScanTest
{
    // With or without a variable, a namespace, a brace or a trailing comment on the line, and with no
    // space after catch. A filter wrapped onto the next line is flagged too: keep it on the catch line.
    private static readonly Regex BareCatch = new(
        @"catch\s*\(\s*(System\.)?(OperationCanceledException|TaskCanceledException)(\s+\w+)?\s*\)\s*(\{.*|//.*)?$",
        RegexOptions.Compiled);

    // A catch-all whose whole filter is "not a cancellation": it lets a timeout escape code meant never to
    // throw. The token-scoped form, "|| !token.IsCancellationRequested", does not match.
    private static readonly Regex CatchAllExcludingEveryCancellation = new(
        @"when\s*\(\s*\w+\s+is\s+not\s+(System\.)?OperationCanceledException\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void Every_cancellation_catch_names_the_token_it_is_about()
    {
        var offenders = Offenders(BareCatch, out var scanned);

        // A scan over nothing passes too, so prove it read the tree.
        Assert.True(scanned > 100, $"scanned only {scanned} files");
        Assert.True(offenders.Count == 0, $"add a when filter: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void No_catch_all_lets_a_timeout_escape_by_excluding_every_cancellation()
    {
        var offenders = Offenders(CatchAllExcludingEveryCancellation, out var scanned);

        Assert.True(scanned > 100, $"scanned only {scanned} files");
        Assert.True(
            offenders.Count == 0,
            $"filter on the token the code owns, \"|| !token.IsCancellationRequested\": {string.Join(", ", offenders)}");
    }

    private static List<string> Offenders(Regex pattern, out int scanned)
    {
        var lines = RepoFiles.ReadSourceLines();
        scanned = lines.Select(line => line.File).Distinct().Count();
        return lines
            .Where(line => pattern.IsMatch(line.Text))
            .Select(line => $"{line.File}:{line.Number}")
            .ToList();
    }
}
