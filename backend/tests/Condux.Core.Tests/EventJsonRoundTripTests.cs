using System.Text.Json;
using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The stored payload is a default-serialized <see cref="Event"/> (the consumer writes it, the control-plane
/// reads it back for read-time symbolication, ADR-0028). This pins that an Event round-trips through default
/// System.Text.Json with the frame fields symbolication rewrites — including the new abs_path + debug images.
/// </summary>
public sealed class EventJsonRoundTripTests
{
    [Fact]
    public void Event_round_trips_through_default_json_serialization()
    {
        var original = new Event
        {
            EventId = "abc",
            Release = "app@1",
            DebugImages = [new DebugImage { Type = "sourcemap", CodeFile = "https://x/a.min.js", DebugId = "d1" }],
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "TypeError",
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame
                            {
                                InApp = true, Filename = "a.min.js", AbsPath = "https://x/a.min.js",
                                Function = "n", Lineno = 1, Colno = 2,
                            },
                        ],
                    },
                },
            ],
        };

        var back = JsonSerializer.Deserialize<Event>(JsonSerializer.Serialize(original))!;

        var frame = back.Exceptions[0].Stacktrace!.Frames[0];
        Assert.Equal("a.min.js", frame.Filename);
        Assert.Equal("https://x/a.min.js", frame.AbsPath);
        Assert.Equal(1, frame.Lineno);
        Assert.True(frame.InApp);
        Assert.Equal("sourcemap", back.DebugImages[0].Type);
        Assert.Equal("d1", back.DebugImages[0].DebugId);
    }
}
