namespace Condux.Core.CveScanning;

/// <summary>
/// Maps an event's reported platform to the package ecosystem its dependency versions belong to,
/// spelled exactly as OSV spells it. One entry per platform any SDK reports, ours or a stock Sentry
/// one. This is a lookup table and nothing else belongs in it.
///
/// <para><b>The spelling is load bearing, not cosmetic.</b> OSV validates the shape of a query and not
/// its values. Asked about lodash on ecosystem "NPM" or "nodejs" rather than "npm" it answers zero
/// advisories with HTTP 200, which is indistinguishable from a package that is genuinely unaffected.
/// So a wrong string here does not fail loudly, it silently reports safety. Every value below was
/// checked against the live OSV API on 2026-09-03 and is pinned by test.</para>
///
/// <para>An unrecognised platform maps to null rather than to a best guess, for the same reason: an
/// absent ecosystem is a knowable unknown, and a guessed one is a confident wrong answer.</para>
/// </summary>
public static class PackageEcosystems
{
    private static readonly Dictionary<string, string> ByPlatform = new(StringComparer.OrdinalIgnoreCase)
    {
        // Our JS core reports "javascript" from every JS package, since @condux/browser, /node, /edge
        // and /nextjs all build on it and none override the field. Stock Sentry SDKs report "node" for
        // a server runtime. Both are npm.
        ["javascript"] = "npm",
        ["node"] = "npm",
        ["python"] = "PyPI",
        ["go"] = "Go",
        ["ruby"] = "RubyGems",
        ["php"] = "Packagist",
        // The JVM SDK hardcodes "java" for Kotlin and Scala too, so one entry covers the platform.
        ["java"] = "Maven",
        ["csharp"] = "NuGet",
    };

    /// <summary>
    /// The OSV ecosystem for a platform, or null when there is no entry for it. Null is the honest
    /// answer for a platform with no package ecosystem we can name (cocoa, native, rust, dart and
    /// anything a future SDK reports), and callers must treat it as unknown rather than as a default.
    /// </summary>
    public static string? ToEcosystem(string? platform) =>
        platform is { Length: > 0 } p && ByPlatform.TryGetValue(p, out var ecosystem) ? ecosystem : null;

    /// <summary>
    /// A scanner's spelling of an ecosystem folded to the OSV one, so two names for the same thing
    /// compare equal.
    ///
    /// <para>This is needed because <see cref="CveFinding.Ecosystem"/> carries whichever vocabulary its
    /// scanner used. Dependabot, the only scanner wired to the findings surface today, enumerates
    /// "composer, go, maven, npm, nuget, pip, pub, rubygems, rust" (checked against GitHub's REST
    /// documentation on 2026-09-04), while OSV names the same ecosystems "Packagist", "Go", "Maven",
    /// "npm", "NuGet", "PyPI", "Pub", "RubyGems" and "crates.io". Most pairs differ only in case, but
    /// three are different words, and two of those three are ecosystems we map a platform to. Without
    /// this fold a Python or PHP finding could never line up with an observed module and would read as
    /// "not observed" for ever, which is precisely the silent negative ADR-0041 exists to avoid.</para>
    ///
    /// <para>An unknown name is returned trimmed rather than dropped, so two unrecognised spellings of
    /// the same thing still compare equal to each other while never matching a known one.</para>
    ///
    /// <para>Deliberately NOT used by <c>EcosystemManifests</c>, which keys on Dependabot's names and
    /// whose only live producer is the Dependabot scanner. Folding there would change the CVE bump
    /// path to fix a case it cannot currently reach.</para>
    /// </summary>
    public static string Canonical(string? ecosystem)
    {
        var name = ecosystem?.Trim() ?? "";
        return Aliases.TryGetValue(name, out var canonical) ? canonical : name;
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pip"] = "PyPI",
        ["composer"] = "Packagist",
        ["rust"] = "crates.io",
        // The rest differ only in case, and are listed so the fold is a complete statement of the
        // mapping rather than a patch over the three that happen to bite.
        ["npm"] = "npm",
        ["go"] = "Go",
        ["maven"] = "Maven",
        ["nuget"] = "NuGet",
        ["rubygems"] = "RubyGems",
        ["pub"] = "Pub",
    };
}
