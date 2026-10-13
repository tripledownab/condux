namespace Condux.Core.CveScanning;

/// <summary>
/// Reading a <see cref="Pep440Version"/> from text: the specification's grammar with its normalised
/// spellings, as the type's own summary lists them.
/// </summary>
public sealed partial class Pep440Version
{
    /// <summary>The version, or null when the text is not one.</summary>
    public static Pep440Version? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > SemanticVersion.MaxLength)
        {
            return null;
        }

        var reader = new Reader(text.Trim().ToLowerInvariant());
        reader.Skip("v");

        var first = reader.ReadNumber();
        if (first is null)
        {
            return null;
        }
        var epoch = "0";
        if (reader.Skip("!"))
        {
            epoch = first;
            first = reader.ReadNumber();
            if (first is null)
            {
                return null;
            }
        }
        var release = new List<string> { first };
        while (reader.Peek('.') && reader.IsDigitAt(1))
        {
            reader.Skip(".");
            release.Add(reader.ReadNumber()!);
        }

        var pre = ReadPre(ref reader);
        var post = ReadPost(ref reader);
        var dev = ReadSuffix(ref reader, ["dev"]);
        // Anything the suffixes did not read, a half written one included, fails the end check.
        var hasLocal = reader.Skip("+");
        if ((hasLocal && !reader.ReadLocal()) || !reader.AtEnd)
        {
            return null;
        }
        return new Pep440Version(
            epoch, [.. release], pre is null ? null : (ToKind(pre.Value.Word), pre.Value.Number), post, dev, hasLocal);
    }

    private static readonly string[] PreWords = ["preview", "alpha", "beta", "pre", "rc", "a", "b", "c"];

    private static int ToKind(string word) => word switch
    {
        "a" or "alpha" => 0,
        "b" or "beta" => 1,
        _ => 2,
    };

    private static (string Word, string Number)? ReadPre(ref Reader reader)
    {
        var mark = reader.Position;
        reader.SkipSeparator();
        foreach (var word in PreWords)
        {
            if (reader.Skip(word))
            {
                return (word, ReadNumberAfterWord(ref reader));
            }
        }
        reader.Position = mark;
        return null;
    }

    private static string? ReadPost(ref Reader reader)
    {
        // The implicit form, "1.0-1", takes a dash and a number with nothing between.
        if (reader.Peek('-') && reader.IsDigitAt(1))
        {
            reader.Skip("-");
            return reader.ReadNumber();
        }
        return ReadSuffix(ref reader, ["post", "rev", "r"]);
    }

    private static string? ReadSuffix(ref Reader reader, string[] words)
    {
        var mark = reader.Position;
        reader.SkipSeparator();
        foreach (var word in words)
        {
            if (reader.Skip(word))
            {
                return ReadNumberAfterWord(ref reader);
            }
        }
        reader.Position = mark;
        return null;
    }

    // A suffix's number may follow a separator, or be left out and read as 0. The separator is taken
    // either way, as packaging takes it: "1.0.post-" is 1.0.post0.
    private static string ReadNumberAfterWord(ref Reader reader)
    {
        reader.SkipSeparator();
        return reader.ReadNumber() ?? "0";
    }

    private struct Reader(string text)
    {
        public int Position { get; set; }

        public readonly bool AtEnd => Position == text.Length;

        public readonly bool Peek(char c) => Position < text.Length && text[Position] == c;

        public readonly bool IsDigitAt(int offset) =>
            Position + offset < text.Length && char.IsAsciiDigit(text[Position + offset]);

        public bool Skip(string word)
        {
            // A word running past the end compares unequal, so this needs no length check of its own.
            if (string.CompareOrdinal(text, Position, word, 0, word.Length) != 0)
            {
                return false;
            }
            Position += word.Length;
            return true;
        }

        public void SkipSeparator()
        {
            if (Position < text.Length && text[Position] is '.' or '-' or '_')
            {
                Position++;
            }
        }

        // Digits with their leading zeros dropped, since "01" and "1" are one number here.
        public string? ReadNumber()
        {
            var start = Position;
            while (Position < text.Length && char.IsAsciiDigit(text[Position]))
            {
                Position++;
            }
            return Position == start ? null : DigitStrings.TrimLeadingZeros(text[start..Position]);
        }

        // Alphanumeric segments separated by '.', '-' or '_', to the end of the text.
        public bool ReadLocal()
        {
            var segment = 0;
            for (; Position < text.Length; Position++)
            {
                if (char.IsAsciiLetterOrDigit(text[Position]))
                {
                    segment++;
                }
                else if (text[Position] is '.' or '-' or '_' && segment > 0)
                {
                    segment = 0;
                }
                else
                {
                    return false;
                }
            }
            return segment > 0;
        }
    }
}
