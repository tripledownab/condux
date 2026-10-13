using Condux.Core.DebugFiles;
using System.Text.Json;

namespace Condux.Symbolizer;

/// <summary>
/// Reads one line of llvm-symbolizer's JSON output (`--output-style=JSON`): an object whose "Symbol" array
/// holds the inline chain, innermost first. Unknown parts come back as "??" or an empty string and are
/// dropped to null here, so a caller never shows the parser's placeholder as if it were a name. Nothing the
/// parser names is second-guessed here: neither a frame's line nor its function's declared line tells a
/// DWARF answer from a symbol-table one, since real code reports 0 for the first (optimized code) or for
/// both (line-tables-only builds, compiler-made functions). An address outside the image is answered with
/// a symbol from the symbol table, so the caller sends only addresses inside it, by the event's image size.
/// </summary>
internal static class SymbolizerOutput
{
    public static IReadOnlyList<ResolvedFrame> ToFrames(string line)
    {
        // The parser is fed files a stranger chose, so its output is not trusted to be well formed either.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return [];
        }

        using var _ = document;
        if (!document.RootElement.TryGetProperty("Symbol", out var symbols) || symbols.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var frames = new List<ResolvedFrame>();
        foreach (var symbol in symbols.EnumerateArray())
        {
            var frame = new ResolvedFrame(
                Known(symbol, "FunctionName"), Known(symbol, "FileName"), Number(symbol, "Line"), Number(symbol, "Column"));
            if (frame.Function is not null || frame.File is not null)
            {
                frames.Add(frame);
            }
        }
        return frames;
    }

    private static string? Known(JsonElement symbol, string name) =>
        symbol.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text != "??"
            ? text
            : null;

    private static int Number(JsonElement symbol, string name) =>
        symbol.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : 0;
}
