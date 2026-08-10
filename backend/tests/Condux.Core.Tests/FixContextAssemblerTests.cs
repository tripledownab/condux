using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Repos;
using Xunit;

namespace Condux.Core.Tests;

public class FixContextAssemblerTests
{
    private static readonly IReadOnlyList<CodeMapping> Mappings =
        [new CodeMapping(Guid.NewGuid(), Guid.NewGuid(), "/app/dist/", "src/")];

    // A realistic event: an in-app crash through two dist files (one hit twice), a framework frame that is
    // not in-app, an email in the error value, and a token in a breadcrumb.
    private static Event SampleEvent() => new()
    {
        Message = "unhandled",
        Exceptions =
        [
            new ExceptionValue
            {
                Type = "TypeError",
                Value = "Cannot read balance, contact admin@acme.io",
                Stacktrace = new Stacktrace
                {
                    Frames =
                    [
                        new Frame { Filename = "/app/node_modules/express/lib.js", Function = "handle", Lineno = 10 },
                        new Frame { Filename = "/app/dist/checkout.js", Function = "processPayment", Lineno = 88, InApp = true },
                        new Frame { Filename = "/app/dist/checkout.js", Function = "charge", Lineno = 92, InApp = true },
                        new Frame { Filename = "/app/dist/cart.js", Function = "total", Lineno = 5, InApp = true },
                    ],
                },
            },
        ],
        Breadcrumbs =
        [
            new Breadcrumb { Category = "ui", Message = "user clicked pay", Level = Level.Info },
            new Breadcrumb { Message = "auth with token sk-ABCDEF1234567890", Level = Level.Debug },
        ],
    };

    [Fact]
    public void Scopes_to_in_app_frames_resolved_to_repo_paths()
    {
        var context = FixContextAssembler.Assemble(SampleEvent(), Mappings);

        // Only in-app frames, resolved via the mapping and deduped in order; the framework frame is excluded.
        Assert.Equal(["src/checkout.js", "src/cart.js"], context.ScopedPaths);
        Assert.Contains("Relevant files:", context.Prompt);
        Assert.Contains("- src/checkout.js", context.Prompt);
        // The culprit is the newest in-app frame, resolved to its repo path.
        Assert.Contains("Culprit: total (src/cart.js:5)", context.Prompt);
    }

    [Fact]
    public void Double_scrubs_secrets_from_the_prompt()
    {
        var prompt = FixContextAssembler.Assemble(SampleEvent(), Mappings).Prompt;

        Assert.Contains("Error: TypeError: Cannot read balance", prompt);
        Assert.DoesNotContain("admin@acme.io", prompt); // email redacted
        Assert.DoesNotContain("sk-ABCDEF1234567890", prompt); // token redacted
        Assert.Contains("[redacted]", prompt);
    }

    [Fact]
    public void Round_trips_the_stored_payload_back_into_a_prompt()
    {
        // The control-plane deserializes the ClickHouse `payload` (written with default options) before
        // assembling, so prove that exact round-trip works here in CI.
        var payload = JsonSerializer.Serialize(SampleEvent());
        var restored = JsonSerializer.Deserialize<Event>(payload)!;

        var context = FixContextAssembler.Assemble(restored, Mappings);

        Assert.Contains("TypeError", context.Prompt);
        Assert.Equal(["src/checkout.js", "src/cart.js"], context.ScopedPaths);
    }

    [Fact]
    public void Falls_back_to_the_message_when_there_is_no_exception()
    {
        var context = FixContextAssembler.Assemble(
            new Event { Message = "Kafka broker unreachable" }, Mappings);

        Assert.Contains("Error: Kafka broker unreachable", context.Prompt);
        Assert.Empty(context.ScopedPaths);
        Assert.DoesNotContain("Relevant files:", context.Prompt);
    }

    [Fact]
    public void Attributes_the_release_and_its_commit_when_known()
    {
        var context = FixContextAssembler.Assemble(
            SampleEvent(), Mappings, new ReleaseContext("2.1.0", "9f3ac21"));

        Assert.Contains("This error first appeared in release 2.1.0, built from commit 9f3ac21.", context.Prompt);
    }

    [Fact]
    public void Attributes_the_release_without_a_commit_when_the_release_is_unrecorded()
    {
        // The issue names a first_release the model should know, but no release row maps it to a commit.
        var context = FixContextAssembler.Assemble(
            SampleEvent(), Mappings, new ReleaseContext("2.1.0", null));

        Assert.Contains("This error first appeared in release 2.1.0.", context.Prompt);
        Assert.DoesNotContain("built from commit", context.Prompt);
    }

    [Fact]
    public void Omits_the_release_line_when_there_is_no_release()
    {
        var context = FixContextAssembler.Assemble(SampleEvent(), Mappings);

        Assert.DoesNotContain("first appeared in release", context.Prompt);
    }

    [Fact]
    public void Culprit_path_is_the_newest_in_app_frame_resolved_to_a_repo_path()
    {
        // The newest in-app frame is cart.js (the crash site); the framework frame is ignored.
        Assert.Equal("src/cart.js", FixContextAssembler.CulpritPath(SampleEvent(), Mappings));
        // No in-app frame → nothing to blame.
        Assert.Null(FixContextAssembler.CulpritPath(new Event { Message = "x" }, Mappings));
    }

    [Fact]
    public void Names_the_suspect_commit_when_known()
    {
        var context = FixContextAssembler.Assemble(
            SampleEvent(), Mappings, new ReleaseContext("2.1.0", "9f3ac21"),
            new SuspectCommitContext("def456", "Refactor charge()", "janedev"));

        Assert.Contains(
            "Suspect commit (last change to the culprit file): def456 by janedev \"Refactor charge()\".",
            context.Prompt);
    }

    [Fact]
    public void Omits_the_suspect_line_when_there_is_no_suspect()
    {
        var context = FixContextAssembler.Assemble(SampleEvent(), Mappings, new ReleaseContext("2.1.0", null));

        Assert.DoesNotContain("Suspect commit", context.Prompt);
    }
}
