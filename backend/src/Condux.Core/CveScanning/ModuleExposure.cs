namespace Condux.Core.CveScanning;

/// <summary>
/// One dependency version observed actually running, read back out of the inventory (ADR-0041). The
/// read-side counterpart to <see cref="ReleaseModule"/>, which is what the consumer is about to write.
/// They differ because the read is aggregated: many sightings across many releases collapse into one
/// of these.
///
/// <para><see cref="Release"/> is the most recent release seen running this version in this
/// environment, not every release that ever did. That is the question worth answering, because an old
/// release running a vulnerable version is history and the current one running it is an incident.</para>
/// </summary>
public readonly record struct ObservedModule(
    string Ecosystem,
    string Package,
    string Version,
    string Environment,
    string Release,
    DateTimeOffset LastSeen);

/// <summary>
/// What can honestly be said about whether a finding's vulnerable package is actually running.
///
/// <para><b>ADR-0041 names three states and this enum has two.</b> The third, a confirmed match
/// against an advisory's vulnerable range, needs a matcher, and the first slice deliberately ships
/// without one. Declaring the value before anything can produce it would put an unreachable branch in
/// every consumer and a state in the API that never arrives. It is added by the slice that can emit
/// it, and adding it is additive.</para>
/// </summary>
public enum ModuleExposureState
{
    /// <summary>
    /// Nothing was observed for this package. Normal, and it means exactly what it says: an SDK that
    /// sends no module list, a language whose SDK does not send one yet, an OTLP-only project, and a
    /// package that genuinely is not installed all look identical from here. <b>It must never be
    /// presented as "not affected".</b>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A version of this package was observed running. Whether that version falls inside the
    /// advisory's vulnerable range is not asserted; the observed version and the advisory's fixed
    /// version are both shown and the reader compares them.
    /// </summary>
    Running = 1,
}

/// <summary>The exposure verdict for one finding, with the sightings it was drawn from.</summary>
public readonly record struct ModuleExposure(
    ModuleExposureState State,
    IReadOnlyList<ObservedModule> Observed);

/// <summary>
/// Decides a finding's exposure from what was observed. Pure, and the single home of the rule: a
/// client that recomputed "running means the list is non-empty" would be a second statement of it,
/// free to drift the day a third state arrives.
/// </summary>
public static class ModuleExposures
{
    /// <summary>
    /// The exposure for one finding. <paramref name="observed"/> is the whole set read for the
    /// project, not a pre-filtered one, so the matching rule stays here rather than being split
    /// between a query and a caller.
    /// </summary>
    public static ModuleExposure For(CveFinding finding, IReadOnlyList<ObservedModule> observed)
    {
        IReadOnlyList<ObservedModule> matches = [.. observed.Where(module => Matches(finding, module))];
        return new ModuleExposure(
            matches.Count == 0 ? ModuleExposureState.Unknown : ModuleExposureState.Running, matches);
    }

    /// <summary>
    /// Whether a sighting is of the package a finding names. Both halves are compared leniently on
    /// purpose, because every mistake in this direction produces a false "not observed", and this
    /// feature exists to avoid exactly that reading.
    ///
    /// <para>The ecosystem is folded through <see cref="PackageEcosystems.Canonical"/> because a
    /// finding carries its scanner's vocabulary and a sighting carries OSV's. The package is compared
    /// case insensitively because a scanner reports a registry's spelling and an SDK reports the
    /// runtime's.</para>
    ///
    /// <para><b>The package half is enforced twice and both must agree.</b> The reader also filters
    /// server-side on <c>lower(package)</c>, because fetching a project's entire inventory to match it
    /// here would be far worse. That filter is the coarse one and this is the exact one, so loosening
    /// the rule here alone changes nothing: the extra rows never arrive. Change both or neither.</para>
    ///
    /// <para><b>Known gap:</b> PyPI also folds <c>-</c>, <c>_</c> and <c>.</c> to one another
    /// (PEP 503), so <c>zope.interface</c> and <c>zope-interface</c> are one project there and do not
    /// match here. That is per-ecosystem normalisation, which this slice does not do, and its failure
    /// is a row reading as unknown rather than a wrong match.</para>
    /// </summary>
    private static bool Matches(CveFinding finding, ObservedModule module) =>
        string.Equals(finding.Package, module.Package, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            PackageEcosystems.Canonical(finding.Ecosystem),
            PackageEcosystems.Canonical(module.Ecosystem),
            StringComparison.OrdinalIgnoreCase);
}
