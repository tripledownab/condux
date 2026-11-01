using System.Text.Json;
using Condux.ControlPlane.Issues;
using Condux.Core.Events;
using Condux.Core.Repos;
using Condux.Storage.ClickHouse;
using Xunit;

namespace Condux.ControlPlane.Tests;

public class FrameLinksTests
{
    private static readonly RepoLink Api = new(Guid.NewGuid(), 7, "acme/api", "main", DateTimeOffset.UnixEpoch);
    private static readonly RepoLink Web = new(Guid.NewGuid(), 7, "acme/web", "trunk", DateTimeOffset.UnixEpoch);

    private static StoredEvent Stored(params Frame[] frames) => Stored(new Event
    {
        Exceptions = [new ExceptionValue { Type = "Error", Stacktrace = new Stacktrace { Frames = frames } }],
    });

    private static StoredEvent Stored(Event e) =>
        new("e1", "", "error", "", "", "", JsonSerializer.Serialize(e));

    private static Frame InApp(string filename, int line = 3) => new() { Filename = filename, Lineno = line, InApp = true };

    [Fact]
    public void Lists_each_frame_that_has_a_link_by_its_position_in_stored_order()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "src/", "services/api/src/") };
        var library = new Frame { Filename = "src/vendor.ts", Lineno = 1, InApp = false };

        var links = FrameLinks.ToLinks([Stored(library, InApp("src/a.ts"), InApp("lib/b.ts"))], [Api], mappings, []);

        Assert.Equal(
            [new FrameLink(1, "https://github.com/acme/api/blob/main/services/api/src/a.ts#L3")],
            Assert.Single(links));
    }

    // The issue page used to read only the first repository's mappings, so a frame mapped into a second
    // repository never linked.
    [Fact]
    public void Links_into_whichever_repository_the_applying_mapping_names()
    {
        var mappings = new[]
        {
            new CodeMapping(Guid.NewGuid(), Api.Id, "api/", ""),
            new CodeMapping(Guid.NewGuid(), Web.Id, "web/", "app/"),
        };

        var links = FrameLinks.ToLinks([Stored(InApp("api/a.ts"), InApp("web/b.ts"))], [Api, Web], mappings, []);

        Assert.Equal(
            [
                new FrameLink(0, "https://github.com/acme/api/blob/main/a.ts#L3"),
                new FrameLink(1, "https://github.com/acme/web/blob/trunk/app/b.ts#L3"),
            ],
            Assert.Single(links));
    }

    [Fact]
    public void Reads_only_the_primary_exception()
    {
        var e = new Event
        {
            Exceptions =
            [
                new ExceptionValue { Stacktrace = new Stacktrace { Frames = [InApp("src/cause.ts")] } },
                new ExceptionValue { Stacktrace = new Stacktrace { Frames = [InApp("src/a.ts"), InApp("src/b.ts")] } },
            ],
        };
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };

        var links = Assert.Single(FrameLinks.ToLinks([Stored(e)], [Api], mappings, []));

        Assert.Equal(
            [
                new FrameLink(0, "https://github.com/acme/api/blob/main/src/a.ts#L3"),
                new FrameLink(1, "https://github.com/acme/api/blob/main/src/b.ts#L3"),
            ],
            links);
    }

    [Fact]
    public void An_event_that_cannot_be_read_or_has_no_exception_gets_no_links()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };
        var unreadable = new StoredEvent("e2", "", "error", "", "", "", "{not json");

        var links = FrameLinks.ToLinks([unreadable, Stored(new Event())], [Api], mappings, []);

        Assert.All(links, Assert.Empty);
    }

    [Fact]
    public void A_mapping_for_a_repository_no_longer_linked_links_nothing()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), "", "") };

        Assert.Empty(Assert.Single(FrameLinks.ToLinks([Stored(InApp("src/a.ts"))], [Api], mappings, [])));
    }

    [Fact]
    public void Two_repositories_mapping_the_same_stack_root_link_nothing()
    {
        var mappings = new[]
        {
            new CodeMapping(Guid.NewGuid(), Api.Id, "src/", "api/src/"),
            new CodeMapping(Guid.NewGuid(), Web.Id, "src/", "web/src/"),
        };

        Assert.Empty(Assert.Single(FrameLinks.ToLinks([Stored(InApp("src/a.ts"))], [Api, Web], mappings, [])));
    }

    private static Release Ran(RepoLink repo, string version, string commit) =>
        new(Guid.NewGuid(), 7, repo.Id, version, commit, DateTimeOffset.UnixEpoch);

    private static StoredEvent InRelease(string? release, params Frame[] frames) => Stored(new Event
    {
        Release = release,
        Exceptions = [new ExceptionValue { Type = "Error", Stacktrace = new Stacktrace { Frames = frames } }],
    });

    // The line comes from the event, so the file has to be the one of the event's release, or the anchor
    // lands on whatever the branch holds today.
    [Fact]
    public void Links_the_commit_the_events_release_was_recorded_at()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };

        var links = FrameLinks.ToLinks(
            [InRelease("1.4.0", InApp("src/a.ts")), InRelease("1.5.0", InApp("src/a.ts"))], [Api], mappings,
            [Ran(Api, "1.4.0", "0123abc"), Ran(Api, "1.5.0", "4567def")]);

        Assert.Equal(
            [
                [new FrameLink(0, "https://github.com/acme/api/blob/0123abc/src/a.ts#L3")],
                [new FrameLink(0, "https://github.com/acme/api/blob/4567def/src/a.ts#L3")],
            ],
            links);
    }

    [Fact]
    public void An_unrecorded_release_or_none_links_the_default_branch()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };

        var links = FrameLinks.ToLinks(
            [InRelease("9.9.9", InApp("src/a.ts")), InRelease(null, InApp("src/a.ts"))], [Api], mappings,
            [Ran(Api, "1.4.0", "0123abc")]);

        Assert.All(links, frames => Assert.Equal(
            [new FrameLink(0, "https://github.com/acme/api/blob/main/src/a.ts#L3")], frames));
    }

    // A release names a commit of the repository it was recorded against, which another repository does
    // not hold.
    [Fact]
    public void A_release_recorded_against_another_repository_links_the_default_branch()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };

        var links = FrameLinks.ToLinks(
            [InRelease("1.4.0", InApp("src/a.ts"))], [Api, Web], mappings, [Ran(Web, "1.4.0", "0123abc")]);

        Assert.Equal([new FrameLink(0, "https://github.com/acme/api/blob/main/src/a.ts#L3")], Assert.Single(links));
    }

    // The admin route stores a commit unchecked, and a stray space escapes to a link that answers 404.
    [Theory]
    [InlineData("../../evil")]
    [InlineData("0123abc ")]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("0123ab")]
    [InlineData("v1.4.0")]
    public void A_stored_commit_that_is_not_a_commit_id_falls_back_to_the_default_branch(string commit)
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };

        var links = FrameLinks.ToLinks(
            [InRelease("1.4.0", InApp("src/a.ts"))], [Api], mappings, [Ran(Api, "1.4.0", commit)]);

        Assert.Equal([new FrameLink(0, "https://github.com/acme/api/blob/main/src/a.ts#L3")], Assert.Single(links));
    }

    [Fact]
    public void A_sha256_commit_id_is_linked()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };
        var commit = new string('a', 64);

        var links = FrameLinks.ToLinks(
            [InRelease("1.4.0", InApp("src/a.ts"))], [Api], mappings, [Ran(Api, "1.4.0", commit)]);

        Assert.Equal([new FrameLink(0, $"https://github.com/acme/api/blob/{commit}/src/a.ts#L3")], Assert.Single(links));
    }

    // The other release lookups skip a blank release, so a blank one never matches a blank version here.
    [Fact]
    public void A_blank_release_matches_no_recorded_release()
    {
        var mappings = new[] { new CodeMapping(Guid.NewGuid(), Api.Id, "", "") };

        var links = FrameLinks.ToLinks(
            [InRelease("", InApp("src/a.ts"))], [Api], mappings, [Ran(Api, "", "0123abc")]);

        Assert.Equal([new FrameLink(0, "https://github.com/acme/api/blob/main/src/a.ts#L3")], Assert.Single(links));
        Assert.Empty(FrameLinks.ToReleaseVersions([InRelease(""), InRelease(" ")]));
    }

    [Fact]
    public void Release_versions_are_read_once_each_from_the_events()
    {
        var versions = FrameLinks.ToReleaseVersions(
            [InRelease("1.4.0"), InRelease("1.4.0"), InRelease(null), InRelease("1.5.0")]);

        Assert.Equal(["1.4.0", "1.5.0"], versions);
    }
}
