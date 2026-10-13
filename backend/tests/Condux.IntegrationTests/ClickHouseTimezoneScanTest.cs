using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every time function in a ClickHouse query names UTC in its arguments, directly or through the time it
/// is given (toStartOfHour(now('UTC') - ...) is a UTC cut). The time columns declare no zone, so a time built
/// from a number or from now(), and an hour or day cut from a column, are in the server's own timezone
/// unless the call names one: right on the bundled UTC server, shifted on any other.
/// <see cref="ClickHouseTimezoneTest"/> catches the shifts that change a result; this catches the rest,
/// such as a window that starts half an hour off, before anyone has to notice them. It cannot see a time
/// column selected bare, which renders as server-zone text; the one query that returns a time as text
/// converts it with toTimeZone, and the timezone test reads that text back.
/// fromUnixTimestamp is refused outright: its two-argument form takes a format string, not a zone, so
/// adding 'UTC' to it silently returns text.
/// </summary>
public class ClickHouseTimezoneScanTest
{
    private static readonly Regex TimeCall = new(
        @"\b(now|now64|today|yesterday|toDateTime|toDateTime64|toDate|toTimeZone|toStartOf\w+|toMonday|toYear|toMonth|toDayOf\w+|toHour|toMinute|toYYYYMM\w*|toRelative\w+|timeSlot|dateDiff|date_diff|dateTrunc|date_trunc|formatDateTime|parseDateTime\w*|fromUnixTimestamp\w*)\(",
        RegexOptions.Compiled);

    [Fact]
    public void Every_time_function_in_a_ClickHouse_query_names_UTC()
    {
        var source = Path.Combine(RepoFiles.FindRoot(), "backend", "src", "Condux.Storage", "ClickHouse");
        var files = Directory.GetFiles(source, "*.cs");
        var calls = 0;
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in TimeCall.Matches(text))
            {
                calls++;
                var arguments = ToArguments(text, match.Index + match.Length);
                if (match.Groups[1].Value.StartsWith("fromUnixTimestamp", StringComparison.Ordinal)
                    || !arguments.Contains("'UTC'", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}{arguments})");
                }
            }
        }

        // A scan that matched nothing passes too, so prove it read the queries.
        Assert.True(calls >= 10, $"matched only {calls} time calls in {files.Length} files");
        Assert.True(offenders.Count == 0, $"name 'UTC' in each: {string.Join("; ", offenders)}");
    }

    // The text between the opening parenthesis at start - 1 and its matching close.
    private static string ToArguments(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return text[start..i];
            }
        }

        return text[start..];
    }
}
