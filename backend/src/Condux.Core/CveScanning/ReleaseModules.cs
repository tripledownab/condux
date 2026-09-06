using Condux.Core.Events;
using Condux.Core.Scrub;

namespace Condux.Core.CveScanning;

/// <summary>One observed dependency: a package at a concrete version, in a named ecosystem.</summary>
public readonly record struct ReleaseModule(string Ecosystem, string Package, string Version);

/// <summary>
/// What of an event's <see cref="Event.Modules"/> is worth recording as a runtime dependency
/// inventory, and what is not. Pure, so every rule below is testable without a store or a broker.
/// The decision belongs here rather than in the consumer because it is the part that can be wrong in
/// a way nobody notices.
/// </summary>
public static class ReleaseModules
{
    /// <summary>
    /// The most modules taken from one event. A real dependency tree runs to a few hundred, so this
    /// bounds a pathological or hostile payload rather than trimming honest ones. Truncation is
    /// reported rather than silent, because a cap nobody is told about reads as full coverage.
    /// </summary>
    public const int MaxPerEvent = 2000;

    /// <summary>
    /// The modules taken from an event, and whether <see cref="MaxPerEvent"/> cut the list short.
    /// An empty list means nothing about this event is recordable, which is a normal outcome.
    /// </summary>
    public readonly record struct Result(IReadOnlyList<ReleaseModule> Modules, bool Truncated);

    /// <summary>
    /// Collect the recordable modules. Returns nothing at all unless the event names both a release
    /// and a platform we can map, because an inventory that cannot be attributed to a release answers
    /// no question worth asking, and one whose ecosystem we guessed is worse than one we do not have.
    /// </summary>
    public static Result Collect(Event e)
    {
        // An inventory is a fact about a release. Without one there is nothing to attach it to, and
        // merging every unreleased build into a single empty-string bucket would invent a release
        // that does not exist.
        if (string.IsNullOrWhiteSpace(e.Release) || e.Modules.Count == 0)
        {
            return new Result([], false);
        }

        if (PackageEcosystems.ToEcosystem(e.Platform) is not { } ecosystem)
        {
            return new Result([], false);
        }

        var modules = new List<ReleaseModule>(Math.Min(e.Modules.Count, MaxPerEvent));
        var truncated = false;
        foreach (var (package, version) in e.Modules)
        {
            if (modules.Count == MaxPerEvent)
            {
                truncated = true;
                break;
            }

            if (IsRecordable(package, version))
            {
                modules.Add(new ReleaseModule(ecosystem, package.Trim(), version.Trim()));
            }
        }

        return new Result(modules, truncated);
    }

    /// <summary>
    /// Whether one entry is worth storing. The redaction check is the one that matters: the ingest
    /// scrub used to replace the version of any package whose name contained "token", "secret",
    /// "password" or "apikey", so every event stored before that was fixed carries "[redacted]" where
    /// jsonwebtoken's version should be. Storing that would render as "running [redacted]", where no
    /// row at all reads as unknown, which is true. The same guard holds if the key rule is ever
    /// widened again.
    /// </summary>
    private static bool IsRecordable(string package, string version) =>
        !string.IsNullOrWhiteSpace(package)
        && !string.IsNullOrWhiteSpace(version)
        && !string.Equals(version, Scrubber.Redacted, StringComparison.Ordinal)
        && !string.Equals(package, Scrubber.Redacted, StringComparison.Ordinal);
}
