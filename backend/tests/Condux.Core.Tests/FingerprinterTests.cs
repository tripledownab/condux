using Condux.Core.Events;
using Condux.Core.Grouping;
using Xunit;

namespace Condux.Core.Tests;

public class FingerprinterTests
{
    private static Event WithException(string type, params (string Module, string Func, bool InApp)[] frames) =>
        new()
        {
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = type,
                    Value = "boom",
                    Stacktrace = new Stacktrace
                    {
                        Frames = frames
                            .Select(f => new Frame { Module = f.Module, Function = f.Func, InApp = f.InApp })
                            .ToList(),
                    },
                },
            ],
        };

    // An unsymbolicated native crash has no module, filename or function on any frame, only addresses.
    private static Event NativeCrash(params (string Image, string Instruction)[] frames) =>
        new()
        {
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "SIGSEGV",
                    Value = "Segmentation fault",
                    Stacktrace = new Stacktrace
                    {
                        Frames = frames
                            .Select(f => new Frame
                            {
                                Package = "MyApp",
                                ImageAddr = f.Image,
                                InstructionAddr = f.Instruction,
                            })
                            .ToList(),
                    },
                },
            ],
        };

    // Without address-aware grouping every native frame reduces to "?:?", so every crash sharing a
    // signal and a frame count collapses into a single issue.
    [Fact]
    public void DistinctNativeCrashSites_DoNotCollapseIntoOneIssue()
    {
        var atOffset40 = Fingerprinter.Compute(NativeCrash(("0x1000", "0x1040")));
        var atOffset80 = Fingerprinter.Compute(NativeCrash(("0x1000", "0x1080")));

        Assert.NotEqual(atOffset40.Fingerprint, atOffset80.Fingerprint);
    }

    // Absolute addresses shift per run under ASLR, so grouping has to key on the offset within the
    // image. Otherwise the same crash splits into a new issue on every launch.
    [Fact]
    public void SameNativeCrashSite_GroupsAcrossAddressSpaceShifts()
    {
        var firstRun = Fingerprinter.Compute(NativeCrash(("0x1000", "0x1040")));
        var afterRelocation = Fingerprinter.Compute(NativeCrash(("0x7f0000", "0x7f0040")));

        Assert.Equal(firstRun.Fingerprint, afterRelocation.Fingerprint);
    }

    [Fact]
    public void SameStack_SameFingerprint()
    {
        var a = WithException("ValueError", ("app.jobs", "run", true));
        var b = WithException("ValueError", ("app.jobs", "run", true));
        Assert.Equal(Fingerprinter.Compute(a).Fingerprint, Fingerprinter.Compute(b).Fingerprint);
    }

    [Fact]
    public void DifferentStack_DifferentFingerprint()
    {
        var a = WithException("ValueError", ("app.jobs", "run", true));
        var b = WithException("ValueError", ("app.web", "handle", true));
        Assert.NotEqual(Fingerprinter.Compute(a).Fingerprint, Fingerprinter.Compute(b).Fingerprint);
    }

    [Fact]
    public void ClientFingerprint_IsHonored()
    {
        var a = new Event { Fingerprint = ["custom-key"], Message = "x" };
        var b = new Event { Fingerprint = ["custom-key"], Message = "totally different" };
        Assert.Equal(Fingerprinter.Compute(a).Fingerprint, Fingerprinter.Compute(b).Fingerprint);
    }

    [Fact]
    public void Title_UsesExceptionTypeAndValue()
    {
        var g = Fingerprinter.Compute(WithException("ValueError", ("app", "f", true)));
        Assert.Equal("ValueError: boom", g.Title);
        Assert.Equal("f", g.Culprit);
    }
}
