using System.Globalization;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Ingest;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Grouping of an obfuscated Flutter crash, from two real sentry_flutter 9.30.1 events captured on an
/// Android device (a release build with --obfuscate --split-debug-info) and trimmed of device data. Their
/// frames carry an instruction address and nothing else. The image base arrives once, in debug_meta.
/// </summary>
public class FlutterNativeGroupingTests
{
    private static Event Load(string name) =>
        SentryParser.ParseEnvelope(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Golden", $"flutter-obfuscated-{name}-envelope.txt"))).Event!;

    [Fact]
    public void TheImageBaseIsRead_AndTheFramesStayAsSent()
    {
        var e = Load("checkout");

        var image = Assert.Single(e.DebugImages);
        Assert.Equal("0x794ea0e000", image.ImageAddr);
        Assert.Equal(3411968, image.ImageSize);
        Assert.All(Assert.Single(e.Exceptions).Stacktrace!.Frames, f => Assert.Null(f.ImageAddr));
    }

    // The reproduction: two different failure sites with the same exception type merged into one issue
    // once their stacks had the same depth, because every frame grouped as "?:?".
    [Fact]
    public void TwoCrashSitesAtTheSameDepth_StayApart()
    {
        var checkout = Load("checkout");
        var login = Load("login");
        var loginFrames = Frames(login);
        login = WithFrames(login, [.. loginFrames.Skip(loginFrames.Count - Frames(checkout).Count)]);
        Assert.Equal(Frames(checkout).Count, Frames(login).Count);

        Assert.NotEqual(Fingerprinter.Compute(checkout).Fingerprint, Fingerprinter.Compute(login).Fingerprint);
    }

    // The other direction: the same crash loaded somewhere else on the next launch must stay one issue.
    [Fact]
    public void OneCrash_GroupsAcrossAddressSpaceShifts()
    {
        var firstLaunch = Load("checkout");
        var relocated = Relocate(firstLaunch, 0x1_0000_0000);

        Assert.Equal(Fingerprinter.Compute(firstLaunch).Fingerprint, Fingerprinter.Compute(relocated).Fingerprint);
        // Equal fingerprints alone would also hold if every frame still reduced to "?:?", so the crash
        // site must keep its offset after the move too.
        Assert.Equal("?+2e7aeb", Fingerprinter.Compute(relocated).Culprit);
    }

    [Fact]
    public void TheCulpritNamesTheCrashingFramesOffset()
    {
        // The last frame is the crash site: 0x794ecf5aeb inside the image loaded at 0x794ea0e000.
        Assert.Equal("?+2e7aeb", Fingerprinter.Compute(Load("checkout")).Culprit);
    }

    private static IReadOnlyList<Frame> Frames(Event e) => Assert.Single(e.Exceptions).Stacktrace!.Frames;

    private static Event WithFrames(Event e, IReadOnlyList<Frame> frames) =>
        e with { Exceptions = [e.Exceptions[0] with { Stacktrace = new Stacktrace { Frames = frames } }] };

    private static Event Relocate(Event e, ulong by) => WithFrames(
        e with { DebugImages = [.. e.DebugImages.Select(i => i with { ImageAddr = Shift(i.ImageAddr, by) })] },
        [.. Frames(e).Select(f => f with { InstructionAddr = Shift(f.InstructionAddr, by) })]);

    private static string? Shift(string? address, ulong by) => address is null
        ? null
        : $"0x{ulong.Parse(address[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) + by:x}";
}
