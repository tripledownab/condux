using Condux.Core.DebugFiles;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.DebugFiles;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The native pass's caches on their own: an answer is found only under the file hash and offset it was
/// kept under, an answer too large is not kept, both caches stay within their limits however much they are
/// offered, and answers and refusals are forgotten once their time is up.
/// </summary>
public sealed class SymbolizerCachesTest
{
    private const string Sha = "aa00000000000000000000000000000000000000000000000000000000000000";
    private const string OtherSha = "bb00000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void An_answer_is_found_only_under_the_file_and_offset_it_was_kept_under()
    {
        using var caches = new SymbolizerCaches();
        IReadOnlyList<ResolvedFrame> frames = [new ResolvedFrame("checkout", "lib/main.dart", 13, 3)];

        caches.KeepFrames(Sha, 0x4600, frames);

        Assert.True(caches.TryGetFrames(Sha, 0x4600, out var kept));
        Assert.Same(frames, kept);
        Assert.False(caches.TryGetFrames(OtherSha, 0x4600, out _));
        Assert.False(caches.TryGetFrames(Sha, 0x4604, out _));
    }

    [Fact]
    public void An_answer_that_resolved_to_no_frames_is_kept_too()
    {
        using var caches = new SymbolizerCaches();

        caches.KeepFrames(Sha, 0x4600, []);

        Assert.True(caches.TryGetFrames(Sha, 0x4600, out var kept));
        Assert.Empty(kept);
    }

    [Fact]
    public void An_answer_past_the_entry_cap_is_not_kept()
    {
        using var caches = new SymbolizerCaches();
        var name = new string('x', (int)(SymbolizerCaches.MaxEntryBytes / 2));

        caches.KeepFrames(Sha, 0x4600, [new ResolvedFrame(name, null, 1, 0)]);
        caches.KeepFrames(Sha, 0x4604, [new ResolvedFrame(name[..^1000], null, 1, 0)]);

        Assert.False(caches.TryGetFrames(Sha, 0x4600, out _));
        Assert.True(caches.TryGetFrames(Sha, 0x4604, out _));
    }

    [Fact]
    public void A_refusal_is_remembered_for_its_time_and_then_forgotten()
    {
        var time = new MovableTime();
        using var caches = new SymbolizerCaches(time);

        caches.KeepRefusal(Sha);
        time.Advance(SymbolizerCaches.RefusalMemory - TimeSpan.FromSeconds(1));
        var during = caches.IsRefused(Sha);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(during);
        Assert.False(caches.IsRefused(Sha));
        Assert.False(caches.IsRefused(OtherSha));
    }

    [Fact]
    public void A_kept_answer_is_used_for_its_time_and_then_forgotten()
    {
        var time = new MovableTime();
        using var caches = new SymbolizerCaches(time);

        caches.KeepFrames(Sha, 0x4600, [new ResolvedFrame("checkout", "lib/main.dart", 13, 3)]);
        time.Advance(SymbolizerCaches.AnswerMemory - TimeSpan.FromSeconds(1));
        var during = caches.TryGetFrames(Sha, 0x4600, out _);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.True(during);
        Assert.False(caches.TryGetFrames(Sha, 0x4600, out _));
    }

    [Fact]
    public void Kept_answers_stay_within_the_byte_limit_however_many_are_offered()
    {
        using var caches = new SymbolizerCaches();
        // Each answer is estimated at well under the entry cap and well over a thousandth of the limit.
        var name = new string('x', 20_000);
        const int Offered = 2_000;

        for (var i = 0; i < Offered; i++)
        {
            caches.KeepFrames(Sha, (ulong)i, [new ResolvedFrame(name, null, 1, 0)]);
        }

        var kept = Enumerable.Range(0, Offered).Count(i => caches.TryGetFrames(Sha, (ulong)i, out _));
        Assert.InRange(kept, 1, SymbolizerCaches.MaxAnswerBytes / (2 * name.Length));
    }

    [Fact]
    public void Remembered_refusals_stay_within_their_count_however_many_are_offered()
    {
        using var caches = new SymbolizerCaches();
        var offered = SymbolizerCaches.MaxRefusedFiles + 500;
        var shas = Enumerable.Range(0, offered).Select(i => i.ToString("x64")).ToList();

        foreach (var sha in shas)
        {
            caches.KeepRefusal(sha);
        }

        Assert.InRange(shas.Count(caches.IsRefused), 1, SymbolizerCaches.MaxRefusedFiles);
    }
}
