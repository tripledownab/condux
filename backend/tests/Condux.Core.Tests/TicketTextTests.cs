using Condux.Core.Events;
using Condux.Core.Tickets;
using Xunit;

namespace Condux.Core.Tests;

public class TicketTextTests
{
    private static TicketSource Source(
        string title = "TypeError: x is undefined", string culprit = "checkout.ts in pay",
        string? release = "1.4.2", string? url = "https://app.condux.test/issues/abc") =>
        new(title, culprit, Level.Error, release, url);

    // CommonMark's list of ASCII punctuation, every one of which may be escaped with a backslash.
    private const string CommonMarkPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    [Fact]
    public void Escapes_every_character_commonmark_lets_a_backslash_escape()
    {
        foreach (var c in CommonMarkPunctuation)
        {
            Assert.Equal($"a\\{c}b", TicketText.EscapeMarkdown($"a{c}b"));
        }
    }

    [Fact]
    public void A_culprit_cannot_open_a_link_or_raw_html_in_the_body()
    {
        var body = TicketText.ToContent(Source(culprit: "[Reset your password](https://evil.test) <img src=x>")).Body;

        // No markup character from the value survives without its backslash. The only unescaped ones left
        // are the bold marks and the link around our own URL.
        var culpritLine = body.Split('\n').Single(l => l.StartsWith("**Culprit:**", StringComparison.Ordinal));
        Assert.DoesNotMatch(@"(?<!\\)[\[\]()<>]", culpritLine["**Culprit:** ".Length..]);
        Assert.Contains("\\[Reset your password\\]\\(https\\:\\/\\/evil\\.test\\)", body);
    }

    [Fact]
    public void A_value_cannot_start_a_block_of_its_own()
    {
        var body = TicketText.ToContent(Source(release: "1.0\n\n# Urgent\n\n> quoted")).Body;

        Assert.Contains("**First seen in:** 1\\.0 \\# Urgent \\> quoted\n", body);
    }

    [Fact]
    public void The_title_is_sent_as_written_because_linear_shows_it_as_plain_text()
    {
        var content = TicketText.ToContent(Source(title: "Cannot read 'id' of [object] *now*"));

        Assert.Equal("Cannot read 'id' of [object] *now*", content.Title);
    }

    [Fact]
    public void The_title_loses_control_characters_and_line_breaks_and_is_capped()
    {
        Assert.Equal("a b c", TicketText.ToContent(Source(title: "a\r\n\tb\u0007c")).Title);
        Assert.DoesNotContain('\n', TicketText.ToContent(Source(title: "one\ntwo")).Title);

        var capped = TicketText.ToContent(Source(title: new string('x', 1000))).Title;
        Assert.Equal(TicketText.MaxTitleLength, capped.Length);
        Assert.EndsWith("…", capped);
    }

    // Only facts that stay true go in, and every one an event chose is escaped like the culprit.
    [Fact]
    public void The_body_carries_first_seen_environment_and_where_with_each_value_escaped()
    {
        var body = TicketText.ToContent(Source() with
        {
            FirstSeen = new DateTimeOffset(2026, 10, 20, 9, 5, 0, TimeSpan.FromHours(2)),
            Environment = "prod [x](https://evil.test)",
            Frames = [new TicketFrame("charge", "checkout/pay.ts", 42), new TicketFrame("<b>", null, 0), new TicketFrame(null, null, 0)],
        }).Body;

        Assert.Contains("**First seen:** 2026-10-20 07:05 UTC", body);
        Assert.Contains("**Environment:** prod \\[x\\]\\(https\\:\\/\\/evil\\.test\\)", body);
        Assert.Contains("- charge \\(checkout\\/pay\\.ts\\:42\\)\n", body);
        Assert.Contains("- \\<b\\>\n", body);
        Assert.Contains("- unknown\n", body);
    }

    [Fact]
    public void Frames_shown_are_the_last_exceptions_in_app_ones_newest_first_and_capped()
    {
        Frame F(string name, bool inApp) => new() { Function = name, Filename = name + ".ts", Lineno = 1, InApp = inApp };
        var sample = new Event
        {
            Exceptions =
            [
                new ExceptionValue { Stacktrace = new Stacktrace { Frames = [F("earlier", true)] } },
                new ExceptionValue { Stacktrace = new Stacktrace { Frames = [F("lib", false), .. Enumerable.Range(0, 7).Select(i => F($"app{i}", true)), F("vendor", false)] } },
            ],
        };

        var frames = TicketText.ToFrames(sample);

        Assert.Equal(["app6", "app5", "app4", "app3", "app2"], frames.Select(f => f.Function));
    }

    // An SDK that marks no frame in-app still says where it broke.
    [Fact]
    public void Frames_fall_back_to_all_of_them_when_none_is_in_app_and_to_none_without_an_event()
    {
        var sample = new Event
        {
            Exceptions = [new ExceptionValue { Stacktrace = new Stacktrace { Frames = [new Frame { Function = "a" }, new Frame { Function = "b" }] } }],
        };

        Assert.Equal(["b", "a"], TicketText.ToFrames(sample).Select(f => f.Function));
        Assert.Empty(TicketText.ToFrames(null));
    }

    [Theory]
    [InlineData(true, true, Level.Error, true)]
    [InlineData(true, true, Level.Fatal, true)]
    [InlineData(true, true, Level.Warning, false)]
    [InlineData(true, false, Level.Error, false)]
    [InlineData(false, true, Level.Error, false)]
    public void Opens_automatically_only_for_a_new_error_in_a_project_that_asked(bool on, bool isNew, Level level, bool expected)
    {
        Assert.Equal(expected, AutoTicketPolicy.ShouldCreate(on, isNew, level));
    }

    private static readonly TicketLabelOption Bug = new("bug", "Bug", null);
    private static readonly TicketLabelOption Fe = new("fe", "Frontend", "area");
    private static readonly TicketLabelOption Be = new("be", "Backend", "area");

    [Fact]
    public void A_save_keeps_offered_labels_with_their_live_names_and_drops_stored_ones_the_tracker_no_longer_offers()
    {
        var choice = TicketLabels.Resolve(["bug", "gone", "bug"], stored: [new("gone", "Gone", null)], offered: [Bug, Bug]);

        Assert.Null(choice.Error);
        Assert.Equal([Bug], choice.Kept);
    }

    [Theory]
    [InlineData("new-and-unknown", "linear_label_unknown")]
    [InlineData(null, "linear_label_invalid")]
    [InlineData(" ", "linear_label_invalid")]
    public void A_save_refuses_a_label_it_cannot_vouch_for(string? id, string error)
    {
        Assert.Equal(error, TicketLabels.Resolve(["bug", id], stored: [], offered: [Bug]).Error);
    }

    // A list cut short at the page ceiling does not prove a saved label is gone, so it is kept as saved.
    [Fact]
    public void A_saved_label_missing_from_an_incomplete_list_is_kept()
    {
        var saved = new TicketLabelOption("far", "Far away", null);

        var choice = TicketLabels.Resolve(["bug", "far"], stored: [saved], offered: [Bug], offeredIsComplete: false);

        Assert.Equal([Bug, saved], choice.Kept);
    }

    [Fact]
    public void A_save_refuses_two_labels_from_one_group_and_allows_one_from_each()
    {
        Assert.Equal("linear_labels_same_group", TicketLabels.Resolve(["fe", "be"], [], [Fe, Be]).Error);
        Assert.Null(TicketLabels.Resolve(["fe", "bug"], [], [Fe, Be, Bug]).Error);
    }

    [Fact]
    public void A_blank_title_still_makes_a_ticket_someone_can_find()
    {
        Assert.Equal("Untitled error", TicketText.ToContent(Source(title: " \n ")).Title);
    }

    [Fact]
    public void The_body_links_back_to_the_issue_and_omits_what_is_missing()
    {
        var full = TicketText.ToContent(Source()).Body;
        Assert.Contains("**Level:** Error", full);
        Assert.Contains("[Open in Condux](https://app.condux.test/issues/abc)", full);

        var bare = TicketText.ToContent(Source(culprit: "", release: null, url: null)).Body;
        Assert.Equal("**Level:** Error\n", bare);
    }
}
