using System.Net;
using System.Text.Json;
using Xunit;

namespace Condux.Sdk.Tests;

/// <summary>
/// Runtime dependency inventory (ADR-0041). The relay reads a top-level "modules" string map, so the
/// wire key is asserted from the serialized body rather than from the payload object: a renamed key or
/// a naming-policy change would be dropped silently by the parser rather than rejected.
/// </summary>
[Collection("module-inventory")]
public class ModuleInventoryTests
{
    private const string Dsn = "https://testkey@ingest.example.test/proj-uuid";

    private static (ConduxClient Client, ScriptedTransport Transport) Build(bool sendModules = true)
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        var options = new ConduxOptions
        {
            Dsn = Dsn,
            Transport = transport,
            Sleep = (_, _) => Task.CompletedTask,
            SendModules = sendModules,
        };
        return (new ConduxClient(options), transport);
    }

    private static JsonElement Event(ScriptedTransport transport, int index) =>
        JsonDocument.Parse(transport.Bodies[index]).RootElement;

    [Fact]
    public async Task DeclaredModulesRideTheWireUnderTheSentryKey()
    {
        ModuleInventory.Reset();
        var (client, transport) = Build();
        ModuleInventory.Set(new Dictionary<string, string>
        {
            ["Newtonsoft.Json"] = "13.0.3",
            ["Confluent.Kafka"] = "2.5.3",
        });

        await client.CaptureMessageAsync("hi", Level.Info);

        var modules = Event(transport, 0).GetProperty("modules");
        Assert.Equal("13.0.3", modules.GetProperty("Newtonsoft.Json").GetString());
        Assert.Equal("2.5.3", modules.GetProperty("Confluent.Kafka").GetString());
    }

    /// <summary>An event that carries no inventory must keep its exact previous wire shape.</summary>
    [Fact]
    public async Task AnEventCarriesNoModulesKeyWhenNoneWereDeclared()
    {
        ModuleInventory.Reset();
        var (client, transport) = Build(sendModules: false);

        await client.CaptureMessageAsync("hi", Level.Info);

        Assert.False(Event(transport, 0).TryGetProperty("modules", out _));
    }

    /// <summary>
    /// What makes the feature affordable: the server deduplicates a release's inventory to one row per
    /// package per day, so repeating the map on every event spends bytes for nothing.
    /// </summary>
    [Fact]
    public async Task TheInventoryRidesTheFirstEventThenWaitsOutTheInterval()
    {
        ModuleInventory.Reset();
        var (client, transport) = Build();
        ModuleInventory.Set(new Dictionary<string, string> { ["Acme.Lib"] = "1.0.0" });

        await client.CaptureMessageAsync("one", Level.Info);
        await client.CaptureMessageAsync("two", Level.Info);
        await client.CaptureMessageAsync("three", Level.Info);

        var carrying = transport.Bodies.Count(body =>
            JsonDocument.Parse(body).RootElement.TryGetProperty("modules", out _));
        Assert.Equal(1, carrying);
    }

    /// <summary>
    /// Repeating matters as much as skipping: the event carrying the inventory can be dropped by a
    /// rate limit before anything parses it, so one attempt per process would lose that day's
    /// inventory outright.
    /// </summary>
    [Fact]
    public void TheInventoryRidesAgainOnceTheIntervalElapses()
    {
        ModuleInventory.Reset();
        ModuleInventory.Set(new Dictionary<string, string> { ["Acme.Lib"] = "1.0.0" });
        var start = DateTimeOffset.UnixEpoch;

        Assert.NotNull(ModuleInventory.Fields(start));
        Assert.Null(ModuleInventory.Fields(start + ModuleInventory.Interval - TimeSpan.FromSeconds(1)));
        Assert.NotNull(ModuleInventory.Fields(start + ModuleInventory.Interval));
    }

    [Fact]
    public void AFreshDeclarationDoesNotWaitOutThePreviousInterval()
    {
        ModuleInventory.Reset();
        ModuleInventory.Set(new Dictionary<string, string> { ["Acme.Lib"] = "1.0.0" });
        var start = DateTimeOffset.UnixEpoch;
        ModuleInventory.Fields(start);

        ModuleInventory.Set(new Dictionary<string, string> { ["Acme.Lib"] = "2.0.0" });

        Assert.Equal("2.0.0", ModuleInventory.Fields(start.AddSeconds(1))!["Acme.Lib"]);
    }

    [Fact]
    public void EntriesAreSortedAndCappedSoEveryEventCarriesTheSameSet()
    {
        ModuleInventory.Reset();
        var many = Enumerable.Range(0, ModuleInventory.MaxModules + 50)
            // Zero padded so lexicographic order is also numeric order, making the survivors predictable.
            .ToDictionary(i => $"Pkg-{i:D5}", _ => "1.0.0");
        ModuleInventory.Set(many);

        var names = ModuleInventory.Fields(DateTimeOffset.UnixEpoch)!.Keys.ToList();

        Assert.Equal(ModuleInventory.MaxModules, names.Count);
        Assert.Equal("Pkg-00000", names[0]);
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    [Fact]
    public void BlankEntriesAreDroppedRatherThanReportedAsVersions()
    {
        ModuleInventory.Reset();
        ModuleInventory.Set(new Dictionary<string, string>
        {
            ["Good"] = "1.0.0",
            ["Blank"] = "",
            [""] = "2.0.0",
        });

        var fields = ModuleInventory.Fields(DateTimeOffset.UnixEpoch)!;
        Assert.Equal(new[] { "Good" }, fields.Keys);
    }

    [Fact]
    public void ClearingRemovesTheInventoryEntirely()
    {
        ModuleInventory.Reset();
        ModuleInventory.Set(new Dictionary<string, string> { ["Acme.Lib"] = "1.0.0" });
        ModuleInventory.Set(null);

        Assert.Null(ModuleInventory.Fields(DateTimeOffset.UnixEpoch));
    }

    /// <summary>
    /// The parse, run against a real deps file rather than a fixture, so the shape it must cope with is
    /// the one the build actually produces. Package ids are asserted to be ids: an entry still carrying
    /// a "/" would mean the id and version were never split, which is how a wrong package name reaches
    /// the server.
    /// </summary>
    [Fact]
    public void ParseReadsTheResolvedPackagesOutOfARealDepsFile()
    {
        var parsed = ModuleInventory.ParseDeps(File.ReadAllText(RealDepsFile()));

        Assert.NotEmpty(parsed);
        foreach (var (id, version) in parsed)
        {
            Assert.False(string.IsNullOrEmpty(id));
            Assert.False(string.IsNullOrEmpty(version));
            Assert.DoesNotContain('/', id);
        }
        // xunit is a real NuGet dependency of this project, so naming it proves the parse found
        // packages rather than merely returning something non-empty.
        Assert.Contains(parsed.Keys, id => id.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The application's own projects are not packages and have no published identity, so reporting one
    /// would name something no advisory can be about.
    /// </summary>
    [Fact]
    public void ParseSkipsProjectReferences()
    {
        var parsed = ModuleInventory.ParseDeps(File.ReadAllText(RealDepsFile()));

        Assert.DoesNotContain("Condux.Sdk", parsed.Keys);
    }

    [Fact]
    public void ParseReturnsNothingForContentItCannotUse()
    {
        Assert.Empty(ModuleInventory.ParseDeps("not json at all"));
        Assert.Empty(ModuleInventory.ParseDeps("{}"));
        Assert.Empty(ModuleInventory.ParseDeps("""{"libraries": "not an object"}"""));
        // An entry with no version is not a package version, so it is skipped rather than reported.
        Assert.Empty(ModuleInventory.ParseDeps("""{"libraries":{"NoVersion":{"type":"package"}}}"""));
    }

    /// <summary>
    /// The clock is a caller-supplied hook, so it must be read once per event. Reading it again for the
    /// inventory would advance a clock that returns a sequence, and would date the event and the
    /// interval decision from two different instants.
    /// </summary>
    [Fact]
    public async Task TheClockIsReadOncePerEvent()
    {
        ModuleInventory.Reset();
        var calls = 0;
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        var client = new ConduxClient(new ConduxOptions
        {
            Dsn = Dsn,
            Transport = transport,
            Sleep = (_, _) => Task.CompletedTask,
            Clock = () => { calls++; return DateTimeOffset.UnixEpoch; },
        });

        await client.CaptureMessageAsync("hi", Level.Info);

        Assert.Equal(1, calls);
    }

    private static string RealDepsFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Condux.Sdk.Tests.deps.json");
        Assert.True(File.Exists(path), $"expected the test project's deps file at {path}");
        return path;
    }

    /// <summary>
    /// Construction declares whatever Collect found, replacing anything already declared.
    ///
    /// <para>Asserted by what it CLEARS rather than by what it finds. Under a test host the entry
    /// assembly is the host, so Collect legitimately finds nothing; a test expecting packages here
    /// would be asserting the test runner's layout rather than the SDK. Replacing a stale inventory is
    /// the observable half, and it is the half that matters: without it a second client would inherit
    /// the first one's packages.</para>
    /// </summary>
    [Fact]
    public async Task ConstructionReplacesAnInventoryDeclaredBeforeIt()
    {
        ModuleInventory.Reset();
        ModuleInventory.Set(new Dictionary<string, string> { ["Stale.Pkg"] = "9.9.9" });

        var (client, transport) = Build();
        await client.CaptureMessageAsync("hi", Level.Info);

        var carried = Event(transport, 0).TryGetProperty("modules", out var modules)
            ? modules.EnumerateObject().Select(p => p.Name)
            : [];
        Assert.DoesNotContain("Stale.Pkg", carried);
    }

    /// <summary>
    /// The opt-out is enforced twice: construction declines to collect, and dispatch declines to
    /// attach. The second guard keeps a disabled client honest, because the inventory is process-wide
    /// state that another client may have populated. Declaring one AFTER construction is the only order
    /// that reaches it.
    /// </summary>
    [Fact]
    public async Task ADisabledClientAttachesNothingEvenWhenAnInventoryIsDeclared()
    {
        ModuleInventory.Reset();
        var (client, transport) = Build(sendModules: false);
        ModuleInventory.Set(new Dictionary<string, string> { ["Acme.Lib"] = "1.0.0" });

        await client.CaptureMessageAsync("hi", Level.Info);

        Assert.False(Event(transport, 0).TryGetProperty("modules", out _));
    }
}
