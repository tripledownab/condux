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
        else if (e.Exceptions.Count > 0)
        {
            var ex = e.Exceptions[^1];
            components.Add("exception");
            if (!string.IsNullOrEmpty(ex.Type))
            {
                components.Add(ex.Type);
            }

            var frames = ex.Stacktrace?.Frames ?? [];
            var appFrames = frames.Where(f => f.InApp).ToList();
            var used = appFrames.Count > 0 ? appFrames : [.. frames];
            foreach (var f in used)
            {
                components.Add(FrameComponent(f));
            }
            if (used.Count > 0)
            {
                var top = used[^1];
                culprit = top.Function ?? top.Filename ?? NativeLocation(top) ?? "";
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
    private static string FrameComponent(Frame frame)
    {
        var source = frame.Module ?? frame.Filename;
        if (!string.IsNullOrEmpty(source) || !string.IsNullOrEmpty(frame.Function))
        {
            return $"{source ?? "?"}:{frame.Function ?? "?"}";
        }
        return NativeLocation(frame) ?? "?:?";
    }

    private static string? NativeLocation(Frame frame) =>
        ImageOffset(frame) is { } offset ? $"{frame.Package ?? "?"}+{offset}" : null;

    /// <summary>The frame's offset inside its loaded image. Absolute addresses move between runs under
    /// ASLR, so only the offset is stable enough to group on.</summary>
    private static string? ImageOffset(Frame frame) =>
        TryParseAddress(frame.InstructionAddr, out var instruction)
        && TryParseAddress(frame.ImageAddr, out var image)
        && instruction >= image
            ? (instruction - image).ToString("x", CultureInfo.InvariantCulture)
            : null;

    private static bool TryParseAddress(string? value, out ulong address)
    {
        address = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        var digits = value.AsSpan();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }
        return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static string TitleOf(Event e)
    {
        if (e.Exceptions.Count > 0)
        {
            var ex = e.Exceptions[^1];
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
