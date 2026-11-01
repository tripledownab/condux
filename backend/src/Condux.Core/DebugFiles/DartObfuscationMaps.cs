using System.Text.Json;
using System.Text.RegularExpressions;
using Condux.Core.Events;

namespace Condux.Core.DebugFiles;

/// <summary>
/// A Flutter build's obfuscation map, and the class names it restores in an event. Built with
/// <c>--obfuscate</c>, an app's classes are renamed, so an exception thrown by app code arrives typed with
/// the obfuscated name (<c>xY</c>), and Dart's default <c>toString</c> writes it into the message as
/// <c>Instance of 'xY'</c>. Frames are unaffected, since the debug file's DWARF keeps the real names.
///
/// The map is what <c>--extra-gen-snapshot-options=--save-obfuscation-map=&lt;file&gt;</c> writes: a JSON
/// array of strings, each original name followed by its obfuscated one. A later pair wins over an earlier
/// one with the same obfuscated name. Only a whole exception type, or the whole name inside
/// <c>Instance of '…'</c>, is restored: the map also renames the Flutter framework's classes, so short
/// ordinary words are obfuscated names too, and restoring words inside a longer string would turn them into
/// class names.
/// </summary>
public static partial class DartObfuscationMaps
{
    /// <summary>The most bytes a map upload may carry.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>The most names one event has looked up, the most debug ids it looks them up under, and the
    /// longest name looked up or held in a map. The event's sender chooses the names and images, and the map's
    /// uploader the names written back, so each is held to a fixed bound.</summary>
    public const int MaxNamesPerEvent = 64, MaxImagesLookedUp = 32, MaxNameLength = 256;

    /// <summary>The map keyed by obfuscated name, or why the body is not one.</summary>
    public static (IReadOnlyDictionary<string, string>? Map, string? Rejection) ToMap(ReadOnlySpan<byte> body)
    {
        string[]? names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(body);
        }
        catch (JsonException)
        {
            names = null;
        }
        if (names is null)
        {
            return (null, "the body is not a JSON array of strings; send the file --save-obfuscation-map wrote");
        }
        if (names.Length % 2 != 0)
        {
            return (null, "the map holds an odd number of names; each original name must be followed by its obfuscated one");
        }
        var map = new Dictionary<string, string>(names.Length / 2, StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i += 2)
        {
            if (!IsStorable(names[i]) || !IsStorable(names[i + 1]))
            {
                return (null, $"the map holds a name that is empty, null, longer than {MaxNameLength} characters "
                    + "or holds a NUL character");
            }
            map[names[i + 1]] = names[i];
        }
        return (map, null);
    }

    /// <summary>The debug ids of the event's ELF and Mach-O images, the only kinds a debug file is, those its
    /// frames ran in first, from the crash site backwards and the last exception first, since a Flutter event
    /// can list every image the process loaded and the app's own may sit far down the list.</summary>
    public static IReadOnlyList<string> ToDebugIds(Event e)
    {
        var ids = new List<string>();
        var images = NativeImages.From(e.DebugImages);
        foreach (var ex in e.Exceptions.Reverse())
        {
            var frames = ex.Stacktrace?.Frames ?? [];
            for (var i = frames.Count - 1; i >= 0 && ids.Count < MaxImagesLookedUp; i--)
            {
                AddId(ids, images.Place(frames[i])?.Image);
            }
        }
        foreach (var image in e.DebugImages)
        {
            AddId(ids, image);
        }
        return ids;
    }

    /// <summary>The obfuscated names an event could have restored: each exception's type, and each name in an
    /// <c>Instance of '…'</c> in its message, up to <see cref="MaxNamesPerEvent"/>, the last exception first,
    /// since that is the one the issue page shows.</summary>
    public static IReadOnlyList<string> ToNames(Event e)
    {
        var names = new List<string>();
        for (var i = e.Exceptions.Count - 1; i >= 0 && names.Count < MaxNamesPerEvent; i--)
        {
            var ex = e.Exceptions[i];
            AddName(names, ex.Type);
            if (ex.Value is { } value)
            {
                for (var m = InstanceOf().Match(value); m.Success && names.Count < MaxNamesPerEvent; m = m.NextMatch())
                {
                    AddName(names, m.Groups[1].Value);
                }
            }
        }
        return names;
    }

    /// <summary>The event with each name <paramref name="restored"/> holds put back, or the event itself
    /// when it holds none of them. One event has at most <see cref="MaxNamesPerEvent"/> names put back,
    /// types and messages together, spent from the last exception backwards as <see cref="ToNames"/>
    /// collects them, so what it writes grows by a bounded amount however often its sender repeats a name or
    /// an exception, and the exception the issue page shows is the one restored first.</summary>
    public static Event Restore(Event e, IReadOnlyDictionary<string, string> restored)
    {
        if (restored.Count == 0)
        {
            return e;
        }
        var changed = false;
        var left = MaxNamesPerEvent;
        var exceptions = e.Exceptions.Reverse().Select(ex =>
        {
            var type = left > 0 && ex.Type is { } t && restored.TryGetValue(t, out var original) ? original : ex.Type;
            if (type != ex.Type)
            {
                left--;
            }
            var value = ex.Value is { } v && left > 0
                ? InstanceOf().Replace(v, m =>
                {
                    if (left == 0 || !restored.TryGetValue(m.Groups[1].Value, out var name))
                    {
                        return m.Value;
                    }
                    left--;
                    return $"Instance of '{name}'";
                })
                : ex.Value;
            if (type == ex.Type && value == ex.Value)
            {
                return ex;
            }
            changed = true;
            return ex with { Type = type, Value = value };
        }).ToList();
        exceptions.Reverse();
        return changed ? e with { Exceptions = exceptions } : e;
    }

    // Postgres text holds no NUL, so a name carrying one could be neither stored nor looked up.
    private static bool IsStorable(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && !name.Contains('\0');

    private static void AddName(List<string> names, string? name)
    {
        if (IsStorable(name) && names.Count < MaxNamesPerEvent && !names.Contains(name!, StringComparer.Ordinal))
        {
            names.Add(name!);
        }
    }

    private static void AddId(List<string> ids, DebugImage? image)
    {
        if (ids.Count < MaxImagesLookedUp && image is { Type: "elf" or "macho", DebugId: { } raw }
            && DebugFileFormats.ToStoredDebugId(raw) is { } id && !ids.Contains(id, StringComparer.Ordinal))
        {
            ids.Add(id);
        }
    }

    [GeneratedRegex("Instance of '([^']+)'")]
    private static partial Regex InstanceOf();
}
