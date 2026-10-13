using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Condux.Core.Events;

namespace Condux.Core.Grouping;

/// <summary>The grouping result for an event: a stable fingerprint plus display fields.</summary>
public sealed record Grouping(string Fingerprint, string Title, string Culprit);

/// <summary>
/// Computes a stable grouping fingerprint for an event. Precedence: client-supplied
/// fingerprint → exception type + stack frames (in-app frames preferred) → message.
/// </summary>
public static class Fingerprinter
{
    public static Grouping Compute(Event e)
    {
        var components = new List<string>();
        var culprit = "";

        if (e.Fingerprint.Count > 0)
        {
            components.AddRange(e.Fingerprint);
        }
        else if (EventExceptions.ResolvePrimary(e) is { } ex)
        {
            components.Add("exception");
            if (!string.IsNullOrEmpty(ex.Type))
            {
                components.Add(ex.Type);
            }

            var frames = ex.Stacktrace?.Frames ?? [];
            var images = NativeImages.From(e.DebugImages);
            var appFrames = frames.Where(f => f.InApp).ToList();
            var used = appFrames.Count > 0 ? appFrames : [.. frames];
            foreach (var f in used)
            {
                components.Add(FrameComponent(f, images));
            }
            if (used.Count > 0)
            {
                var top = used[^1];
                culprit = top.Function ?? top.Filename ?? NativeLocation(top, images) ?? "";
            }
        }
        else
        {
            components.Add("message");
            components.Add(e.Message ?? "");
        }

        return new Grouping(Hash(components), TitleOf(e), culprit);
    }

    /// <summary>
    /// How one frame identifies itself for grouping. A symbolicated frame keys on its source location.
    /// An unsymbolicated native frame has no module, filename or function, only addresses, so it keys on
    /// its offset within its binary image instead. Without that every native frame reduces to the same
    /// placeholder and every crash sharing a signal collapses into one issue.
    /// </summary>
    private static string FrameComponent(Frame frame, NativeImages images)
    {
        return NativeImages.IsAddressOnly(frame)
            ? NativeLocation(frame, images) ?? "?:?"
            : $"{frame.Module ?? frame.Filename ?? "?"}:{frame.Function ?? "?"}";
    }

    private static string? NativeLocation(Frame frame, NativeImages images) =>
        images.Place(frame) is { } placed
            ? $"{frame.Package ?? "?"}+{placed.Offset.ToString("x", CultureInfo.InvariantCulture)}"
            : null;

    private static string TitleOf(Event e)
    {
        if (EventExceptions.ResolvePrimary(e) is { } ex)
        {
            if (!string.IsNullOrEmpty(ex.Type))
            {
                return string.IsNullOrEmpty(ex.Value) ? ex.Type : $"{ex.Type}: {ex.Value}";
            }
        }
        return string.IsNullOrEmpty(e.Message) ? "Error" : e.Message;
    }

    private static string Hash(IEnumerable<string> components)
    {
        var joined = string.Join('\n', components);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(bytes)[..32];
    }
}
