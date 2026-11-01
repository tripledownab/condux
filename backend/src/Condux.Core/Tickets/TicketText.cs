using System.Text;
using Condux.Core.Events;

namespace Condux.Core.Tickets;

/// <summary>The issue fields a ticket is built from. Title, culprit, release, environment and frames come
/// from events, so whoever sent the event chose them. Only facts that stay true go in: a ticket is a
/// snapshot nobody updates, so an event count or a users affected figure would be wrong by the next event,
/// and the link to Condux is the live view.</summary>
public sealed record TicketSource(
    string Title, string Culprit, Level Level, string? FirstRelease, string? IssueUrl,
    DateTimeOffset? FirstSeen = null, string? Environment = null, IReadOnlyList<TicketFrame>? Frames = null);

/// <summary>One stack frame as a ticket shows it: where the code is, not what it held.</summary>
public sealed record TicketFrame(string? Function, string? File, int Line);

/// <summary>
/// Builds a ticket's title and body from an issue, for a click and an alert alike, so there is one place
/// where untrusted text meets the tracker's formatting. The title is plain text in Linear and is sent as
/// written, since escaping it would show the escape characters; it only loses control characters and is
/// capped. The body is markdown, so each substituted value is escaped and the markup around it is ours.
/// </summary>
public static class TicketText
{
    // Long enough for any real error title, short enough that a crafted one cannot fill the tracker's view.
    public const int MaxTitleLength = 250;
    private const int MaxValueLength = 1000;

    // Enough frames to say where to look, few enough that the ticket stays a pointer rather than a copy.
    public const int MaxFrames = 5;

    public static TicketContent ToContent(TicketSource source)
    {
        var body = new StringBuilder()
            .Append("**Level:** ").Append(source.Level.ToString()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(source.Culprit))
        {
            body.Append("**Culprit:** ").Append(EscapeMarkdown(source.Culprit)).Append("\n\n");
        }
        if (!string.IsNullOrWhiteSpace(source.FirstRelease))
        {
            body.Append("**First seen in:** ").Append(EscapeMarkdown(source.FirstRelease)).Append("\n\n");
        }
        if (source.FirstSeen is { } firstSeen)
        {
            // Ours, formatted from a timestamp, so there is nothing to escape.
            body.Append("**First seen:** ").Append(firstSeen.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)).Append("\n\n");
        }
        if (!string.IsNullOrWhiteSpace(source.Environment))
        {
            body.Append("**Environment:** ").Append(EscapeMarkdown(source.Environment)).Append("\n\n");
        }
        if (source.Frames is { Count: > 0 } frames)
        {
            body.Append("**Where:**\n\n");
            foreach (var frame in frames.Take(MaxFrames))
            {
                body.Append("- ").Append(ToFrameLine(frame)).Append('\n');
            }
            body.Append('\n');
        }
        // The URL is ours, built from our own base URL and the issue's id, so it is not escaped.
        if (source.IssueUrl is { } url)
        {
            body.Append("[Open in Condux](").Append(url).Append(")\n");
        }
        return new TicketContent(ToTitle(source.Title), body.ToString().TrimEnd() + "\n");
    }

    /// <summary>
    /// The frames worth showing from an event: the in-app frames of the last exception, which is the one the
    /// dashboard renders, newest first because frames arrive oldest first with the crash site last. When no
    /// frame is marked in-app, all of them, since an SDK that marks none still says where it broke.
    /// </summary>
    public static IReadOnlyList<TicketFrame> ToFrames(Event? sample)
    {
        var frames = sample?.Exceptions.LastOrDefault()?.Stacktrace?.Frames ?? [];
        var chosen = frames.Any(f => f.InApp) ? frames.Where(f => f.InApp) : frames;
        return [.. chosen.Reverse().Take(MaxFrames).Select(f => new TicketFrame(f.Function, f.Filename ?? f.Module, f.Lineno))];
    }

    // "function (file:line)", every part escaped, any part missing left out.
    private static string ToFrameLine(TicketFrame frame)
    {
        var where = string.IsNullOrWhiteSpace(frame.File)
            ? ""
            : EscapeMarkdown(frame.File) + (frame.Line > 0 ? "\\:" + frame.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        var function = string.IsNullOrWhiteSpace(frame.Function) ? "" : EscapeMarkdown(frame.Function);
        return (function, where) switch
        {
            ("", "") => "unknown",
            (_, "") => function,
            ("", _) => where,
            _ => $"{function} \\({where}\\)",
        };
    }

    private static string ToTitle(string title)
    {
        var flat = Flatten(title);
        if (flat.Length == 0)
        {
            return "Untitled error";
        }
        return flat.Length <= MaxTitleLength ? flat : flat[..(MaxTitleLength - 1)] + "…";
    }

    /// <summary>
    /// Backslash-escapes every ASCII punctuation character, which CommonMark allows for all of them, so no
    /// value can write markdown link or image syntax, emphasis, a heading or raw HTML, and newlines are
    /// flattened first so it cannot start a block of its own either. It does not stop a renderer that turns a
    /// bare URL into a link on its own; how Linear renders one is not something this repository has checked.
    /// Escaping only the characters known to matter today is how an escape falls behind the renderer.
    /// </summary>
    public static string EscapeMarkdown(string value)
    {
        var flat = Flatten(value);
        if (flat.Length > MaxValueLength)
        {
            flat = flat[..MaxValueLength] + "…";
        }
        var escaped = new StringBuilder(flat.Length * 2);
        foreach (var c in flat)
        {
            if (char.IsAscii(c) && (char.IsPunctuation(c) || char.IsSymbol(c)))
            {
                escaped.Append('\\');
            }
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    // Control characters and line breaks become spaces, and runs of whitespace collapse to one.
    private static string Flatten(string value)
    {
        var flat = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = flat.Length > 0;
                continue;
            }
            if (space)
            {
                flat.Append(' ');
                space = false;
            }
            flat.Append(c);
        }
        return flat.ToString();
    }
}
