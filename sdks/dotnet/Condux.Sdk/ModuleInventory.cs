using System.Text.Json;

namespace Condux.Sdk;

/// <summary>
/// The runtime dependency inventory that rides events (ADR-0041): which NuGet package versions this
/// application actually resolved, as opposed to which ones a project file declares. The relay indexes
/// it so a security advisory can be answered with "and you are running 2.5.3 in production" rather
/// than only "your csproj says so".
///
/// <para>The source is the application's <c>.deps.json</c>, which the SDK writes at build time and
/// which lists the exact resolved package id and version of everything the app depends on. That is the
/// deployed graph, and its ids are the ones advisories are indexed against. It is read rather than
/// enumerated from loaded assemblies on purpose: an assembly name is not a package id, they differ for
/// plenty of packages, and a name that matches no advisory produces a false "not observed".</para>
///
/// <para>Parsed with <see cref="System.Text.Json"/>, which is in the base library, so this SDK stays
/// dependency free.</para>
/// </summary>
/// <remarks>
/// Internal: the only knob a user needs is <see cref="ConduxOptions.SendModules"/>. Every member here
/// is called by the client or by tests, which reach it through InternalsVisibleTo, and the Go SDK keeps
/// its equivalents unexported for the same reason.
/// </remarks>
internal static class ModuleInventory
{
    /// <summary>
    /// The most entries carried on one event. The cap applies after sorting, so which entries survive
    /// is stable across events rather than varying with file order: the server sees one consistent set
    /// instead of a shifting sample.
    /// </summary>
    public const int MaxModules = 1000;

    /// <summary>
    /// How long to wait before repeating the inventory on another event.
    ///
    /// <para>This is what makes the feature affordable. The server deduplicates a release's inventory
    /// down to one row per package per day, so attaching the whole map to every event would spend
    /// bytes for nothing. Repeating on an interval rather than sending once keeps the robustness that
    /// every-event buys: the event carrying the inventory can be dropped by a rate limit or a quota
    /// rejection before anything parses it, so one attempt per process would lose that day's
    /// inventory outright.</para>
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static readonly object Gate = new();
    private static IReadOnlyDictionary<string, string>? modules;
    private static DateTimeOffset? lastAttachedAt;

    /// <summary>
    /// The resolved NuGet packages as an id to version map, empty when there is nothing to read.
    ///
    /// <para>Never throws. A single-file or trimmed publish does not leave a .deps.json on disk, and
    /// an application run in ways the SDK never anticipated may not either. Unknown is the honest
    /// answer there, and it is what the server already understands from every SDK that cannot
    /// enumerate.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Collect()
    {
        try
        {
            var path = FindDepsFile();
            return path is null ? Empty() : ParseDeps(File.ReadAllText(path));
        }
        catch (Exception)
        {
            // This runs during client construction in an application that never asked for a file read,
            // so an unreadable or malformed deps file costs the inventory and nothing else.
            return Empty();
        }
    }

    /// <summary>
    /// The resolved packages named in a deps file's contents. Separate from reading the file so the
    /// parsing, which is the part that can be wrong, is testable without a filesystem: under a test
    /// host the entry assembly is the host rather than the application, so <see cref="Collect"/>
    /// deliberately finds nothing there.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseDeps(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                return Empty();
            }

            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var library in libraries.EnumerateObject())
            {
                // Entries are "<id>/<version>", and "type" separates real packages from the project
                // references that make up the application itself. A project reference has no published
                // identity, so reporting one would name something no advisory can be about.
                if (!library.Value.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "package", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = library.Name.LastIndexOf('/');
                if (separator <= 0 || separator == library.Name.Length - 1)
                {
                    continue;
                }

                found[library.Name[..separator]] = library.Name[(separator + 1)..];
            }

            return found;
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    /// <summary>Declare the package versions for subsequent events; null clears them.</summary>
    public static void Set(IReadOnlyDictionary<string, string>? next)
    {
        lock (Gate)
        {
            // A fresh declaration is news, so let the next event carry it rather than waiting out an
            // interval started by the previous inventory.
            lastAttachedAt = null;

            if (next is null)
            {
                modules = null;
                return;
            }

            var entries = next
                .Where(pair => !string.IsNullOrEmpty(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(MaxModules)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            modules = entries.Count == 0 ? null : entries;
        }
    }

    /// <summary>
    /// The inventory to attach to an event: the full map on the first event and then at most once per
    /// <see cref="Interval"/>, and null otherwise, so an event that carries nothing keeps its exact
    /// previous wire shape.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Fields(DateTimeOffset now)
    {
        lock (Gate)
        {
            if (modules is null)
            {
                return null;
            }
            if (lastAttachedAt is { } last && now - last < Interval)
            {
                return null;
            }

            lastAttachedAt = now;
            return modules;
        }
    }

    /// <summary>Reset the inventory and its interval. Tests only; a process has one dependency graph.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            modules = null;
            lastAttachedAt = null;
        }
    }

    /// <summary>
    /// The application's deps file beside its entry assembly, or null when there is none.
    ///
    /// <para>Falls back to the only such file in the directory, and gives up when there are several,
    /// because at that point there is no way to tell which one is the application. That is not
    /// theoretical: a build that copies a tool alongside the app leaves two, and picking the wrong one
    /// would report the tool's dependencies as the application's, which is a confident wrong answer
    /// rather than a missing one.</para>
    /// </summary>
    private static string? FindDepsFile()
    {
        var directory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (entry is not null)
        {
            var expected = Path.Combine(directory, entry + ".deps.json");
            if (File.Exists(expected))
            {
                return expected;
            }
        }

        var candidates = Directory.GetFiles(directory, "*.deps.json");
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
