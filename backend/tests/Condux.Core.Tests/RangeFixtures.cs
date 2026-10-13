using Condux.Core.CveScanning;

namespace Condux.Core.Tests;

/// <summary>
/// Reads a generated range fixture in <c>Golden/</c> (one line per advisory range, its claimed and its
/// unclaimed versions) and checks <see cref="VulnerableRanges"/> against every version in it. Shared by
/// the ecosystems whose fixtures are generated from the ecosystem's own tool.
/// </summary>
internal static class RangeFixtures
{
    /// <summary>
    /// What the fixture held; the versions the matcher confirmed inside although the tool leaves them
    /// out, and those it confirmed outside although the tool puts them in (the two defects that must
    /// never happen); and those the tool puts in that the matcher left undecided (allowed, and
    /// counted).
    /// </summary>
    public sealed record Result(
        int Ranges,
        int Versions,
        int Claimed,
        int ConfirmedOutside,
        IReadOnlyList<string> OverClaims,
        IReadOnlyList<string> WrongOutside,
        IReadOnlyList<string> Undecided);

    public static Result Check(string fileName, string ecosystem, string tool)
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Golden", fileName))
            .Where(line => !line.StartsWith('#'))
            .ToList();
        List<string> overClaims = [], wrongOutside = [], undecided = [];
        int versions = 0, claimed = 0, confirmedOutside = 0;
        foreach (var fields in lines.Select(line => line.Split('\t')))
        {
            var range = fields[2];
            foreach (var (list, expected) in new[] { (fields[3], true), (fields[4], false) })
            {
                foreach (var version in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    versions++;
                    claimed += expected ? 1 : 0;
                    var confirmed = VulnerableRanges.IsConfirmedInRange(ecosystem, range, version);
                    var line = $"{fields[0]} {fields[1]} \"{range}\" {version}: the fixture, from {tool}, says {expected}";
                    if (confirmed && !expected)
                    {
                        overClaims.Add(line);
                    }
                    if (VulnerableRanges.IsConfirmedOutsideRange(ecosystem, range, version))
                    {
                        confirmedOutside++;
                        if (expected)
                        {
                            wrongOutside.Add(line);
                        }
                    }
                    else if (!confirmed && expected)
                    {
                        undecided.Add(line);
                    }
                }
            }
        }
        return new Result(lines.Count, versions, claimed, confirmedOutside, overClaims, wrongOutside, undecided);
    }
}
