using System.Globalization;
using System.Text.Json;
using Condux.Core.Events;

namespace Condux.Core.Ingest;

/// <summary>The exception side of a Sentry payload: the exception values themselves, the thread that
/// crashed, each frame, and the debug images that let a frame be matched to its source map later.
/// Frames decide grouping and what the fix engine reads, so this is the half worth getting right.
/// </summary>
public static partial class SentryParser
{
    // mechanism.handled distinguishes an unhandled crash from a caught-and-reported error; absent = null.
    private static bool? ParseHandled(JsonElement exItem) =>
        exItem.TryGetMember("mechanism", out var mechanism) && mechanism.ValueKind == JsonValueKind.Object
        && mechanism.TryGetMember("handled", out var handled)
        && handled.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? handled.GetBoolean()
            : null;

    /// <summary>The frame's local variables (Sentry vars), values stringified whatever their JSON
    /// shape so nested structures survive as readable text.</summary>
    private static IReadOnlyDictionary<string, string> ParseVars(JsonElement frame)
    {
        if (!frame.TryGetMember("vars", out var vars) || vars.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }
        var map = new Dictionary<string, string>();
        foreach (var property in vars.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.GetRawText();
        }
        return map;
    }


    private static IReadOnlyList<ExceptionValue> ParseExceptions(JsonElement root)
    {
        if (!TryGetValuesArray(root, "exception", out var values))
        {
            return [];
        }

        // A native crash reports its stack on the crashed thread, leaving the exception carrying only a
        // type and value, so an exception with no frames of its own borrows the thread's. Without this
        // an iOS or NDK crash stores with no stack trace at all.
        var threadStack = ParseCrashedThreadStacktrace(root);

        var list = new List<ExceptionValue>();
        foreach (var item in values.EnumerateArray())
        {
            var own = ParseStacktrace(item);
            list.Add(new ExceptionValue
            {
                Type = GetString(item, "type"),
                Value = GetString(item, "value"),
                Module = GetString(item, "module"),
                Stacktrace = own is { Frames.Count: > 0 } ? own : threadStack ?? own,
                Handled = ParseHandled(item),
            });
        }
        return list;
    }

    /// <summary>The stack of the thread flagged <c>crashed</c>, else the first thread carrying frames.</summary>
    private static Stacktrace? ParseCrashedThreadStacktrace(JsonElement root)
    {
        if (!TryGetValuesArray(root, "threads", out var threads))
        {
            return null;
        }

        Stacktrace? firstWithFrames = null;
        foreach (var thread in threads.EnumerateArray())
        {
            if (ParseStacktrace(thread) is not { Frames.Count: > 0 } stack)
            {
                continue;
            }
            if (GetBool(thread, "crashed"))
            {
                return stack;
            }
            firstWithFrames ??= stack;
        }
        return firstWithFrames;
    }

    private static Stacktrace? ParseStacktrace(JsonElement exItem)
    {
        if (!exItem.TryGetMember("stacktrace", out var st) || st.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!st.TryGetMember("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return new Stacktrace();
        }

        var list = new List<Frame>();
        foreach (var f in frames.EnumerateArray())
        {
            list.Add(new Frame
            {
                Filename = JvmFramePaths.ToSourcePath(GetString(f, "filename"), GetString(f, "module")),
                AbsPath = GetString(f, "abs_path"),
                Function = GetString(f, "function"),
                Module = GetString(f, "module"),
                Lineno = GetInt(f, "lineno"),
                Colno = GetInt(f, "colno"),
                InApp = GetBool(f, "in_app"),
                ContextLine = GetString(f, "context_line"),
                ContextBefore = GetStringArray(f, "pre_context"),
                ContextAfter = GetStringArray(f, "post_context"),
                Vars = ParseVars(f),
                Package = GetString(f, "package"),
                Symbol = GetString(f, "symbol"),
                InstructionAddr = GetString(f, "instruction_addr"),
                ImageAddr = GetString(f, "image_addr"),
                SymbolAddr = GetString(f, "symbol_addr"),
            });
        }
        return new Stacktrace { Frames = list };
    }

    // debug_meta.images[] links a debug id to the built file it identifies, so a frame can later be matched
    // to its uploaded source map (ADR-0028), and says where each native image was loaded, so a frame that
    // carries only an instruction address can be placed inside one. Defensive: skips a missing/malformed
    // debug_meta or image, and an image_size that is not a positive whole number.
    private static IReadOnlyList<DebugImage> ParseDebugImages(JsonElement root)
    {
        if (!root.TryGetMember("debug_meta", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetMember("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<DebugImage>();
        foreach (var img in images.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new DebugImage
            {
                Type = GetString(img, "type"),
                CodeFile = GetString(img, "code_file"),
                DebugId = GetString(img, "debug_id"),
                ImageAddr = GetString(img, "image_addr"),
                ImageSize = ParseImageSize(img),
            });
        }

        return list;
    }

    // Sentry's own protocol docs send it as a number in their elf and macho examples and as a decimal
    // string in the pe one, so both are read. Anything that is not a positive whole number reads as absent.
    private static long? ParseImageSize(JsonElement image)
    {
        if (!image.TryGetMember("image_size", out var size))
        {
            return null;
        }
        var bytes = size.ValueKind switch
        {
            JsonValueKind.Number when size.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                size.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text) => text,
            _ => 0L,
        };
        return bytes > 0 ? bytes : null;
    }
}
