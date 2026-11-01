using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Fails on an <c>Event</c> read from JSON anywhere but <c>EventJson</c>. Its options replace U+0000 in
/// every string, which Postgres cannot store; a read through the serializer's defaults would hand a NUL a
/// sender put in an event to the consumer's issue upsert, which failed, and the drain loop paused before
/// the next message. The scan looks at reads because a read is where the type is named, and the reader
/// replaces whatever a writer let through.
/// </summary>
public partial class EventJsonScanTest
{
    [GeneratedRegex(@"Deserialize(Async)?<Event>|typeof\(Event\)")]
    private static partial Regex EventRead();

    [Fact]
    public void Events_are_read_from_json_only_through_EventJson()
    {
        var elsewhere = RepoFiles.ReadSourceLines()
            .Where(line => Path.GetFileName(line.File) != "EventJson.cs"
                && EventRead().IsMatch(line.Text))
            .Select(line => $"{line.File}:{line.Number}")
            .ToList();

        Assert.True(elsewhere.Count == 0, "Event read from JSON outside EventJson at " + string.Join(", ", elsewhere));
    }

    /// <summary>The scan reads the right files: the one permitted read is there.</summary>
    [Fact]
    public void The_scan_sees_EventJson()
    {
        Assert.Contains(RepoFiles.ReadSourceLines(), line => Path.GetFileName(line.File) == "EventJson.cs"
            && EventRead().IsMatch(line.Text));
    }
}
