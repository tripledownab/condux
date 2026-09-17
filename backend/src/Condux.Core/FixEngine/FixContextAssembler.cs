using System.Text;
using Condux.Core.Events;
using Condux.Core.Repos;
using Condux.Core.Scrub;
using Condux.Core.SourceControl;

namespace Condux.Core.FixEngine;

/// <summary>The scoped, double-scrubbed context for one fix run: a prompt describing the error and the
/// repo-relative files the agent should focus on. Built from the issue's sample event and the repo's code
/// mappings — never the whole repo, never unscrubbed.</summary>
public sealed record FixContext(string Prompt, IReadOnlyList<string> ScopedPaths);

/// <summary>Release attribution for the error (#144): the release the issue was first seen in, and the
/// commit that release was built from when one is recorded. Feeds a bisect hint into the fix prompt — the
/// bug was introduced by <see cref="Version"/>, so the model can reason from the right point in history.</summary>
public sealed record ReleaseContext(string Version, string? CommitSha);

/// <summary>The commit most likely to have introduced the error (#144): the last change to the culprit
/// file as of the first-seen release, with its subject and author. Narrows the model straight to the
/// suspect change. Resolved by the control-plane via GitHub, so it is optional (GitHub may be off).</summary>
public sealed record SuspectCommitContext(string Sha, string Subject, string? Author);

/// <summary>
/// Assembles the fix prompt from a sample <see cref="Event"/>: the error summary and culprit, the in-app
/// frames resolved to repo paths via <see cref="CodeMapper"/>, and recent breadcrumbs. The assembled text is
/// run through <see cref="Scrubber"/> again (the second of the double scrub — the relay scrubbed on ingest),
/// so no secret reaches the model even if one slipped into a value or path. Pure and GitHub-free; the
/// control-plane calls it at RequestFix time (matching the <see cref="Scrubber"/> contract).
/// </summary>
public static class FixContextAssembler
{
    private const int MaxBreadcrumbs = 10;

    public static FixContext Assemble(
        Event sampleEvent, IReadOnlyList<CodeMapping> mappings,
        ReleaseContext? release = null, SuspectCommitContext? suspect = null)
    {
        var frames = PrimaryFrames(sampleEvent);
        var inAppFrames = frames.Where(f => f.InApp && !string.IsNullOrEmpty(f.Filename)).ToList();

        var scopedPaths = ResolveScopedPaths(inAppFrames, mappings);
        var summary = Summarize(EventExceptions.ResolvePrimary(sampleEvent), sampleEvent.Message);
        var culprit = Culprit(inAppFrames.Count > 0 ? inAppFrames[^1] : frames.LastOrDefault(), mappings);
        var breadcrumbs = RecentBreadcrumbs(sampleEvent.Breadcrumbs);

        var prompt = Scrubber.ScrubString(Render(summary, culprit, release, suspect, scopedPaths, breadcrumbs));
        return new FixContext(prompt, scopedPaths);
    }

    /// <summary>The repo path of the culprit frame — the newest in-app frame, resolved via mappings (#144).
    /// Null when the error has no in-app frame. The control-plane blames this file for the suspect commit.</summary>
    public static string? CulpritPath(Event sampleEvent, IReadOnlyList<CodeMapping> mappings)
    {
        var inApp = PrimaryFrames(sampleEvent).Where(f => f.InApp && !string.IsNullOrEmpty(f.Filename)).ToList();
        return inApp.Count == 0 ? null : CodeMapper.Resolve(inApp[^1].Filename!, mappings) ?? inApp[^1].Filename!;
    }

    private static IReadOnlyList<Frame> PrimaryFrames(Event e) =>
        EventExceptions.ResolvePrimary(e)?.Stacktrace?.Frames ?? [];

    // In-app frames resolved to repo paths (falling back to the raw frame path when no mapping matches),
    // deduped in order — the "scoped files" the agent should focus on, never the whole repo.
    //
    // A frame's filename is authored by whoever sent the event, and these paths are fetched from the
    // customer's repository with a token that can read all of it, so a path that leaves the repo root is
    // DROPPED here rather than passed on. Dropped and not thrown: one crafted frame must not deny the fix
    // run to the legitimate frames beside it, and the source-host client refuses such a path anyway
    // (RepoPaths.ToUrlPath), so this is the early half of the same rule rather than the only guard.
    private static IReadOnlyList<string> ResolveScopedPaths(
        IReadOnlyList<Frame> inAppFrames, IReadOnlyList<CodeMapping> mappings)
    {
        var seen = new HashSet<string>();
        var paths = new List<string>();
        foreach (var frame in inAppFrames)
        {
            var path = CodeMapper.Resolve(frame.Filename!, mappings) ?? frame.Filename!;
            if (RepoPaths.IsRepoRelative(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static string Summarize(ExceptionValue? primary, string? message)
    {
        var type = primary?.Type;
        var value = primary?.Value ?? message;
        if (string.IsNullOrEmpty(type))
        {
            return string.IsNullOrEmpty(value) ? "Unknown error" : value;
        }

        return string.IsNullOrEmpty(value) ? type : $"{type}: {value}";
    }

    private static string? Culprit(Frame? frame, IReadOnlyList<CodeMapping> mappings)
    {
        if (frame is null)
        {
            return null;
        }

        var path = frame.Filename is null ? "" : CodeMapper.Resolve(frame.Filename, mappings) ?? frame.Filename;
        var where = frame.Lineno > 0 ? $"{path}:{frame.Lineno}" : path;
        return string.IsNullOrEmpty(frame.Function) ? where : $"{frame.Function} ({where})";
    }

    private static IReadOnlyList<string> RecentBreadcrumbs(IReadOnlyList<Breadcrumb> breadcrumbs)
    {
        var lines = new List<string>();
        foreach (var crumb in breadcrumbs.Where(b => !string.IsNullOrEmpty(b.Message)).TakeLast(MaxBreadcrumbs))
        {
            var category = string.IsNullOrEmpty(crumb.Category) ? "" : $"{crumb.Category}: ";
            lines.Add($"[{crumb.Level.ToString().ToLowerInvariant()}] {category}{crumb.Message}");
        }

        return lines;
    }

    private static string Render(
        string summary, string? culprit, ReleaseContext? release, SuspectCommitContext? suspect,
        IReadOnlyList<string> scopedPaths, IReadOnlyList<string> breadcrumbs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior engineer fixing a production error. Open a draft pull request that");
        sb.AppendLine("addresses the root cause and adds or updates a test. Change only what the fix requires.");
        sb.AppendLine();
        sb.AppendLine(UntrustedText.Guidance);
        sb.AppendLine();

        // The error summary, the culprit and the breadcrumbs are all authored by whoever sent the event,
        // so each goes inside a fence rather than being interpolated beside the instruction above.
        //
        // The culprit is fenced for a reason that is easy to miss: unlike the scoped paths below, it is
        // NOT checked by RepoPaths. Culprit() falls back to the raw frame filename when no code mapping
        // matches, so it carries whatever the frame said.
        UntrustedText.AppendFenced(sb, "error reported by the application", summary);
        UntrustedText.AppendFenced(sb, "culprit frame", culprit);

        // Everything below is interpolated into OUR sentence rather than fenced, because the model has
        // to read it as the task. That makes neutralizing it the whole defence, and it is done ONCE per
        // value here rather than at each use: the CVE assembler wrote this per-use first and promptly
        // grew the gap it invites.
        //
        // None of these is our text. The release version is the event's own `release` field, recorded on
        // the issue by the consumer, so it is as sender-chosen as the error message above. A commit
        // subject and author are written by whoever made the commit. And a scoped path passed
        // RepoPaths.IsRepoRelative, which answers CONTAINMENT ("does this leave the repo root") and says
        // nothing about whether the text can open a region; an earlier comment here claimed that check
        // was enough, which conflated the two.
        var version = UntrustedText.Neutralize(release?.Version);
        var releaseCommit = UntrustedText.Neutralize(release?.CommitSha);
        var suspectSha = UntrustedText.Neutralize(suspect?.Sha);
        var suspectAuthor = UntrustedText.Neutralize(suspect?.Author);
        var suspectSubject = UntrustedText.Neutralize(suspect?.Subject);

        // Release attribution (#144): a bisect hint. The fix still lands on the current branch head — the
        // release and commit only tell the model when the bug entered, not where to branch from.
        if (release is not null)
        {
            sb.Append("This error first appeared in release ").Append(version);
            if (releaseCommit.Length > 0)
            {
                sb.Append(", built from commit ").Append(releaseCommit);
            }
            sb.AppendLine(".");
        }

        // Suspect commit (#144): the last change to the culprit file as of that release — the most likely
        // origin of the bug, so the model can start there. Still just a hint, not the branch base.
        if (suspect is not null)
        {
            sb.Append("Suspect commit (last change to the culprit file): ").Append(suspectSha);
            if (suspectAuthor.Length > 0)
            {
                sb.Append(" by ").Append(suspectAuthor);
            }
            if (suspectSubject.Length > 0)
            {
                sb.Append(" \"").Append(suspectSubject).Append('"');
            }
            sb.AppendLine(".");
        }

        if (scopedPaths.Count > 0)
        {
            sb.AppendLine("Relevant files:");
            foreach (var path in scopedPaths)
            {
                sb.Append("- ").AppendLine(UntrustedText.Neutralize(path));
            }
        }

        if (breadcrumbs.Count > 0)
        {
            UntrustedText.AppendFenced(
                sb, "recent breadcrumbs", string.Join('\n', breadcrumbs.Select(line => $"- {line}")));
        }

        return sb.ToString().TrimEnd();
    }
}
