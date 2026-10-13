namespace Condux.Core.CveScanning;

/// <summary>
/// How a Maven advisory range reads: <see cref="MavenVersion"/> order where it is decided, and no
/// confirmation where it is not. Checked against real GitHub Maven advisory ranges, decided by Maven
/// 3.9.16 and 4.0.0-rc-7 themselves, in MavenVulnerableRangesTests.
/// </summary>
internal sealed class MavenVersionScheme : IVersionScheme<MavenVersion>
{
    public static readonly MavenVersionScheme Instance = new();

    private MavenVersionScheme()
    {
    }

    /// <summary>
    /// The running version. A pre-release is not set aside as it is for npm and PyPI: a Maven range
    /// has no rule that leaves pre-releases out, and <see cref="MavenVersion"/> decides one only where
    /// every Maven order agrees.
    /// </summary>
    public MavenVersion? ParseRunning(string version) => MavenVersion.Parse(version);

    /// <summary>
    /// A bound, read exactly either way. A partial one needs no padding, since the order already reads
    /// 1.2 as 1.2.0.
    /// </summary>
    public MavenVersion? ParseBound(string op, string text, bool exact) => MavenVersion.Parse(text);

    /// <summary>Order, where the order is decided, and null where it is not.</summary>
    public bool? Admits(string op, MavenVersion bound, MavenVersion running) =>
        running.CompareTo(bound) is { } order ? VulnerableRanges.AdmitsByOrder(op, order) : null;
}
