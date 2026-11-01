using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Condux.Storage.Postgres;

/// <summary>
/// The one way a <c>jsonb</c> parameter is added, with any U+0000 its JSON holds replaced by U+FFFD.
/// </summary>
/// <remarks>
/// Postgres refuses <c>\u0000</c> in <c>jsonb</c> with 22P05, a different code from the 22021 it gives for
/// <c>text</c>. The JSON written here is built from caller and model text (a fix's base branch and prompt,
/// a runner's summary in an audit entry, an advisory's evidence), so a NUL in any of them failed the
/// statement: a runner fix request answered 503, and an audit row was dropped unseen. The JSON is
/// rewritten rather than each caller's value, since many callers build it and the parameter is where they
/// all meet. JsonbParameterScanTest fails on a <c>jsonb</c> parameter added another way.
/// </remarks>
internal static class PostgresJson
{
    public static void AddJsonb(this NpgsqlCommand cmd, string name, string json) =>
        cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = ToStorable(json) });

    /// <summary>
    /// The JSON with each <c>\u0000</c> escape written as <c>\ufffd</c>, and a raw U+0000 outside an escape
    /// replaced too (one straight after a backslash is invalid JSON, which no serializer writes). An escaped
    /// backslash is copied with the character after it, so the text <c>\\u0000</c> (a literal backslash
    /// followed by "u0000") is left as it is.
    /// </summary>
    internal static string ToStorable(string json)
    {
        if (!json.Contains("\\u0000", StringComparison.OrdinalIgnoreCase) && !json.Contains('\0', StringComparison.Ordinal))
        {
            return json;
        }

        var result = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '\0')
            {
                result.Append('\uFFFD');
            }
            else if (c != '\\' || i + 1 == json.Length)
            {
                result.Append(c);
            }
            else if (json[i + 1] == 'u' && i + 5 < json.Length && json.AsSpan(i + 2, 4).SequenceEqual("0000"))
            {
                result.Append("\\ufffd");
                i += 5;
            }
            else
            {
                result.Append(c).Append(json[i + 1]);
                i++;
            }
        }
        return result.ToString();
    }
}
