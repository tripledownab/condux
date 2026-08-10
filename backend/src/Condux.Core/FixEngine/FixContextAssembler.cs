using System.Text;
using Condux.Core.Events;
using Condux.Core.Repos;
using Condux.Core.Scrub;

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
        var summary = Summarize(Primary(sampleEvent), sampleEvent.Message);
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

    // The primary exception (the last one — the crash, mirroring the ClickHouse writer) and its frames.
    private static ExceptionValue? Primary(Event e) => e.Exceptions.Count > 0 ? e.Exceptions[^1] : null;

    private static IReadOnlyList<Frame> PrimaryFrames(Event e) => Primary(e)?.Stacktrace?.Frames ?? [];

    // In-app frames resolved to repo paths (falling back to the raw frame path when no mapping matches),
    // deduped in order — the "scoped files" the agent should focus on, never the whole repo.
    private static IReadOnlyList<string> ResolveScopedPaths(
        IReadOnlyList<Frame> inAppFrames, IReadOnlyList<CodeMapping> mappings)
    {
        var seen = new HashSet<string>();
        var paths = new List<string>();
        foreach (var frame in inAppFrames)
        {
            var path = CodeMapper.Resolve(frame.Filename!, mappings) ?? frame.Filename!;
            if (seen.Add(path))
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
        sb.Append("Error: ").AppendLine(summary);
        if (!string.IsNullOrEmpty(culprit))
        {
            sb.Append("Culprit: ").AppendLine(culprit);
        }

        // Release attribution (#144): a bisect hint. The fix still lands on the current branch head — the
        // release and commit only tell the model when the bug entered, not where to branch from.
        if (release is { } r)
        {
            sb.Append("This error first appeared in release ").Append(r.Version);
            if (!string.IsNullOrEmpty(r.CommitSha))
            {
                sb.Append(", built from commit ").Append(r.CommitSha);
            }
            sb.AppendLine(".");
        }

        // Suspect commit (#144): the last change to the culprit file as of that release — the most likely
        // origin of the bug, so the model can start there. Still just a hint, not the branch base.
        if (suspect is { } s)
        {
            sb.Append("Suspect commit (last change to the culprit file): ").Append(s.Sha);
            if (!string.IsNullOrEmpty(s.Author))
            {
                sb.Append(" by ").Append(s.Author);
            }
            if (!string.IsNullOrEmpty(s.Subject))
            {
                sb.Append(" \"").Append(s.Subject).Append('"');
            }
            sb.AppendLine(".");
        }

        if (scopedPaths.Count > 0)
        {
            sb.AppendLine("Relevant files:");
            foreach (var path in scopedPaths)
            {
                sb.Append("- ").AppendLine(path);
            }
        }

        if (breadcrumbs.Count > 0)
        {
            sb.AppendLine("Recent breadcrumbs:");
            foreach (var line in breadcrumbs)
            {
                sb.Append("- ").AppendLine(line);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
