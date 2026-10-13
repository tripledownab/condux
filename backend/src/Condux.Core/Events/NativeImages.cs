using System.Globalization;

namespace Condux.Core.Events;

/// <summary>Where a native frame ran: its offset inside the image it ran in, and that image when the event
/// lists it. Absolute addresses move between runs under ASLR, so only the offset is stable.</summary>
public readonly record struct NativePlacement(ulong Offset, DebugImage? Image);

/// <summary>
/// The rule that places a native frame in its binary image. Grouping keys an unsymbolicated frame on the
/// offset it yields, and native symbolication resolves that offset in the image's debug file, so both read
/// it from here: two copies of the rule would group a frame by one image and symbolicate it in another.
/// </summary>
public sealed class NativeImages
{
    private readonly Entry[] _sorted;

    private NativeImages(Entry[] sorted) => _sorted = sorted;

    private readonly record struct Entry(ulong Start, long? Size, DebugImage Image);

    /// <summary>
    /// The event's native images, parsed and sorted once so each frame can be placed by a binary search.
    /// The sender chooses both the frame count and the image count, so scanning every image for every frame
    /// would make the work their product. The size and then the debug id break a tie between equal bases,
    /// which keeps the choice, and so the fingerprint and the file read for it, independent of the order the
    /// images arrived in.
    /// </summary>
    public static NativeImages From(IReadOnlyList<DebugImage> images)
    {
        var parsed = new List<Entry>();
        foreach (var image in images)
        {
            if (TryParseAddress(image.ImageAddr, out var start))
            {
                parsed.Add(new Entry(start, image.ImageSize, image));
            }
        }
        parsed.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : (a.Size ?? 0) != (b.Size ?? 0) ? (a.Size ?? 0).CompareTo(b.Size ?? 0)
            : string.CompareOrdinal(a.Image.DebugId, b.Image.DebugId));
        return new NativeImages([.. parsed]);
    }

    /// <summary>Whether the frame names nothing but where it ran, so grouping keys it on its offset and native
    /// symbolication may name it.</summary>
    public static bool IsAddressOnly(Frame frame) =>
        string.IsNullOrEmpty(frame.Module ?? frame.Filename) && string.IsNullOrEmpty(frame.Function);

    /// <summary>
    /// The frame's offset inside the image it ran in, or null when it carries no usable address. A frame may
    /// name its own image base; an obfuscated Flutter build sends none and lists its images once in
    /// <c>debug_meta</c>. The image is then the one with the highest base at or below the address, and the
    /// address must also fall inside that image's size when it has one. An image with no size ends where
    /// the next one starts, which is the rule Sentry's protocol states.
    /// </summary>
    public NativePlacement? Place(Frame frame)
    {
        if (!TryParseAddress(frame.InstructionAddr, out var instruction))
        {
            return null;
        }
        if (TryParseAddress(frame.ImageAddr, out var own))
        {
            return instruction >= own ? new NativePlacement(instruction - own, Exactly(own)) : null;
        }

        var found = Below(instruction);
        return found >= 0 && (_sorted[found].Size is not { } size || instruction - _sorted[found].Start < (ulong)size)
            ? new NativePlacement(instruction - _sorted[found].Start, _sorted[found].Image)
            : null;
    }

    // The last image whose base is at or below the address, or -1.
    private int Below(ulong address)
    {
        int low = 0, high = _sorted.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_sorted[middle].Start <= address)
            {
                (found, low) = (middle, middle + 1);
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    // The listed image a frame's own base names, so a frame that states its base still finds its debug id.
    private DebugImage? Exactly(ulong start)
    {
        var found = Below(start);
        return found >= 0 && _sorted[found].Start == start ? _sorted[found].Image : null;
    }

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
}
