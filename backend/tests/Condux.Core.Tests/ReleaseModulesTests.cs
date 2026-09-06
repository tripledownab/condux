using Condux.Core.CveScanning;
using Condux.Core.Events;
using Condux.Core.Scrub;
using Xunit;

namespace Condux.Core.Tests;

public class ReleaseModulesTests
{
    private static Event EventWith(
        Dictionary<string, string> modules, string? release = "1.4.2", string? platform = "javascript") =>
        new() { Release = release, Platform = platform, Modules = modules };

    [Fact]
    public void CollectsEachModuleUnderTheEcosystemThePlatformMapsTo()
    {
        var result = ReleaseModules.Collect(EventWith(new Dictionary<string, string>
        {
            ["lodash"] = "4.17.11",
            ["express"] = "4.16.0",
        }));

        Assert.False(result.Truncated);
        Assert.Equal(2, result.Modules.Count);
        Assert.Contains(new ReleaseModule("npm", "lodash", "4.17.11"), result.Modules);
        Assert.Contains(new ReleaseModule("npm", "express", "4.16.0"), result.Modules);
    }

    /// <summary>
    /// The guard that matters most. Events stored before the scrub was fixed carry "[redacted]" where
    /// the version of any package named like a credential should be, and a future widening of the key
    /// rule would reintroduce it. Recording that would render as "running [redacted]"; recording
    /// nothing reads as unknown, which is true. Note the sibling package still comes through, so this
    /// drops an entry rather than the event.
    /// </summary>
    [Fact]
    public void SkipsARedactedVersionButKeepsTheRestOfTheEvent()
    {
        var result = ReleaseModules.Collect(EventWith(new Dictionary<string, string>
        {
            ["jsonwebtoken"] = Scrubber.Redacted,
            ["lodash"] = "4.17.11",
        }));

        Assert.Equal([new ReleaseModule("npm", "lodash", "4.17.11")], result.Modules);
    }

    /// <summary>
    /// An inventory is a fact about a release, so without one there is nothing to attach it to.
    /// Collecting anyway would merge every unreleased build into one empty string bucket and invent a
    /// release that does not exist.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CollectsNothingWithoutARelease(string? release) =>
        Assert.Empty(ReleaseModules.Collect(
            EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" }, release: release)).Modules);

    /// <summary>
    /// An unmapped platform yields nothing rather than an entry with a blank or guessed ecosystem.
    /// A row whose ecosystem is wrong is worse than an absent row, because it would be queried
    /// confidently and answered wrongly.
    /// </summary>
    [Theory]
    [InlineData("cocoa")]
    [InlineData("rust")]
    [InlineData(null)]
    public void CollectsNothingForAPlatformWithNoEcosystem(string? platform) =>
        Assert.Empty(ReleaseModules.Collect(
            EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" }, platform: platform)).Modules);

    [Fact]
    public void SkipsEntriesMissingAPackageOrAVersion()
    {
        var result = ReleaseModules.Collect(EventWith(new Dictionary<string, string>
        {
            ["blank-version"] = "",
            ["whitespace-version"] = "   ",
            [""] = "1.0.0",
            ["lodash"] = "4.17.11",
        }));

        Assert.Equal([new ReleaseModule("npm", "lodash", "4.17.11")], result.Modules);
    }

    /// <summary>
    /// The cap bounds a hostile or broken payload, and it reports that it fired. A cap nobody is told
    /// about reads to the next person as full coverage, which is the failure this flag exists to
    /// prevent.
    /// </summary>
    [Fact]
    public void CapsTheModulesTakenFromOneEventAndSaysSo()
    {
        var many = new Dictionary<string, string>();
        for (var i = 0; i < ReleaseModules.MaxPerEvent + 50; i++)
        {
            many[$"pkg-{i}"] = "1.0.0";
        }

        var result = ReleaseModules.Collect(EventWith(many));

        Assert.True(result.Truncated);
        Assert.Equal(ReleaseModules.MaxPerEvent, result.Modules.Count);
    }

    [Fact]
    public void DoesNotReportTruncationWhenTheEventFitsExactly()
    {
        var exact = new Dictionary<string, string>();
        for (var i = 0; i < ReleaseModules.MaxPerEvent; i++)
        {
            exact[$"pkg-{i}"] = "1.0.0";
        }

        var result = ReleaseModules.Collect(EventWith(exact));

        Assert.False(result.Truncated);
        Assert.Equal(ReleaseModules.MaxPerEvent, result.Modules.Count);
    }

    [Fact]
    public void TrimsSurroundingWhitespaceFromAPackageAndVersion()
    {
        var result = ReleaseModules.Collect(EventWith(new Dictionary<string, string>
        {
            [" lodash "] = " 4.17.11 ",
        }));

        Assert.Equal([new ReleaseModule("npm", "lodash", "4.17.11")], result.Modules);
    }
}
