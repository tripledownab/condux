namespace Condux.Core.Http;

/// <summary>
/// Reading an HTTP <c>Link</c> header (RFC 8288) for the URL of the next page. Pure, so every form the
/// grammar allows can be tested without a server.
///
/// <para>Parsed by the RFC rather than by the one spelling a provider happens to send today, because a
/// spelling the parser misses reads as "no next page", which a caller then reports as a complete list.
/// That is the failure a paged read exists to avoid, and it is silent.</para>
/// </summary>
public static class LinkHeaders
{
    /// <summary>
    /// The target of the first link whose relation types include <c>next</c>, or null when there is none.
    /// The target is returned exactly as written, relative or absolute and with its escapes intact:
    /// resolving it, and deciding whether it may be followed, are the caller's.
    /// </summary>
    public static string? FindNextUrl(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            foreach (var link in SplitOutsideQuotes(value, ','))
            {
                if (TryReadLink(link, out var target, out var parameters)
                    && HasRelation(parameters, "next"))
                {
                    return target;
                }
            }
        }

        return null;
    }

    // A link-value is "<" URI-Reference ">" followed by its parameters. The target is cut at the angle
    // brackets, never at a separator, since RFC 3986 allows both ";" and "," inside a URI.
    private static bool TryReadLink(string link, out string target, out string parameters)
    {
        target = parameters = "";
        var open = link.IndexOf('<');
        var close = open < 0 ? -1 : link.IndexOf('>', open + 1);
        if (close < 0)
        {
            return false;
        }

        target = link[(open + 1)..close];
        parameters = link[(close + 1)..];
        return true;
    }

    // RFC 8288: a parameter value is a token or a quoted string (section 3), a rel value may hold several
    // space-separated relation types and only the first rel parameter counts (section 3.3), and relation
    // types compare case-insensitively (section 2.1).
    private static bool HasRelation(string parameters, string relation)
    {
        foreach (var parameter in SplitOutsideQuotes(parameters, ';'))
        {
            var equals = parameter.IndexOf('=');
            var name = (equals < 0 ? parameter : parameter[..equals]).Trim();
            if (!name.Equals("rel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = equals < 0 ? "" : parameter[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(type => type.Equals(relation, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    // Splits on a separator that is not inside a quoted string or a <...> target, so a quoted parameter
    // or a URI containing the separator stays whole. A backslash escapes the next character in a quote.
    private static IEnumerable<string> SplitOutsideQuotes(string text, char separator)
    {
        var start = 0;
        var quoted = false;
        var inTarget = false;
        for (var at = 0; at < text.Length; at++)
        {
            var character = text[at];
            if (quoted)
            {
                if (character == '\\')
                {
                    at++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
            }
            else if (inTarget)
            {
                inTarget = character != '>';
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == '<')
            {
                inTarget = true;
            }
            else if (character == separator)
            {
                yield return text[start..at];
                start = at + 1;
            }
        }

        yield return text[start..];
    }
}
