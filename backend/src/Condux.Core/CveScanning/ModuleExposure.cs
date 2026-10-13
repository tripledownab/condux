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
    DateTimeOffset LastSeen)
{
    /// <summary>
    /// Whether this version was matched inside the finding's vulnerable range by
    /// <see cref="VulnerableRanges"/>. Set per finding by <see cref="ModuleExposures.For"/>, and false
    /// as read. <b>False means not confirmed, never not affected</b>: it covers a version outside the
    /// range and one the matcher cannot decide alike.
    /// </summary>
    public bool Confirmed { get; init; }
}

/// <summary>
/// What can honestly be said about whether a finding's vulnerable package is actually running.
///
/// <para>The states only ever climb on evidence. Nothing here says a package is safe: a version
/// outside the range and one the matcher cannot decide both stay <see cref="Running"/>.</para>
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
    /// A version of this package was observed running, and none was confirmed inside the advisory's
    /// vulnerable range. That is not a verdict of safety: the observed version and the advisory's fixed
    /// version are both shown and the reader compares them.
    /// </summary>
    Running = 1,

    /// <summary>
    /// At least one observed version was matched inside the advisory's vulnerable range. The sightings
    /// that matched carry <see cref="ObservedModule.Confirmed"/>.
    /// </summary>
    Exposed = 2,
}

/// <summary>The exposure verdict for one finding, with the sightings it was drawn from.</summary>
public readonly record struct ModuleExposure(
    ModuleExposureState State,
    IReadOnlyList<ObservedModule> Observed);

/// <summary>
/// Decides a finding's exposure from what was observed. Pure, and the single home of the rule: a
/// client that recomputed "running means the list is non-empty" would be a second statement of it,
/// and one that tested for Running alone would read every Exposed finding as Unknown.
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
        IReadOnlyList<ObservedModule> matches =
        [
            .. observed
                .Where(module => Matches(finding, module))
                .Select(module => module with
                {
                    // The registry's own name rule, unlike the lenient match above. npm names are
                    // case sensitive and the registry holds distinct packages that differ only in case
                    // ("Proto" and "proto"), so there a lenient match may be another package, which may
                    // show as Running and must never be confirmed. A scanner that miscases an npm name
                    // loses the confirmation, which is the safe side.
                    Confirmed = PackageNames.AreSame(module.Ecosystem, finding.Package, module.Package)
                        && VulnerableRanges.IsConfirmedInRange(
                            finding.Ecosystem, finding.VulnerableRange, module.Version),
                }),
        ];
        var state = matches switch
        {
            [] => ModuleExposureState.Unknown,
            _ when matches.Any(module => module.Confirmed) => ModuleExposureState.Exposed,
            _ => ModuleExposureState.Running,
        };
        return new ModuleExposure(state, matches);
    }

    /// <summary>
    /// Whether a sighting is of the package a finding names, for Running. Both halves are compared
    /// leniently on purpose, because every mistake in the other direction produces a false "not
    /// observed", and this feature exists to avoid exactly that reading. Confirmation does not rely on
    /// it: <see cref="For"/> applies the registry's own name rule before it confirms.
    ///
    /// <para>The ecosystem is folded through <see cref="PackageEcosystems.Canonical"/> because a
    /// finding carries its scanner's vocabulary and a sighting carries OSV's. The package is compared by
    /// <see cref="PackageNames.ToMatchKey"/>, case insensitively and on PyPI with its separators
    /// folded, because a scanner reports a registry's spelling and an SDK reports the runtime's.</para>
    ///
    /// <para><b>The package half is enforced twice and both must agree</b> (pinned by
    /// ModuleExposureTests here and ReleaseModuleFlowTest at the reader). The reader also filters
    /// server-side on <see cref="PackageNames.ToFilterKey"/>, because fetching a project's entire
    /// inventory to match it here would be far worse. That filter is the coarse one and this is the
    /// exact one, so loosening the rule here alone changes nothing: the extra rows never arrive. Change
    /// both or neither.</para>
    /// </summary>
    private static bool Matches(CveFinding finding, ObservedModule module) =>
        PackageNames.ToMatchKey(module.Ecosystem, finding.Package) == PackageNames.ToMatchKey(module.Ecosystem, module.Package)
        && string.Equals(
            PackageEcosystems.Canonical(finding.Ecosystem),
            PackageEcosystems.Canonical(module.Ecosystem),
            StringComparison.OrdinalIgnoreCase);
}
