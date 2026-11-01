using Condux.Core.SourceMaps;
using Condux.Storage.DebugFiles;
using Condux.Storage.ObjectStore;
using Condux.Storage.SourceMaps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The symbolication registration on its own, as the consumer uses it: a service that registers no
/// repositories itself must still start with both passes on. Needs no store, since nothing here connects.
/// </summary>
public sealed class EventSymbolicationRegistrationTest
{
    private static ServiceProvider Build(
        Dictionary<string, string?> settings, ObjectStoreAccess access = ObjectStoreAccess.Read)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddEventSymbolication(cfg, "Host=unused", access);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Dictionary<string, string?> Storage() => new()
    {
        ["CONDUX_S3_ENDPOINT"] = "http://s3.invalid:9000",
        ["CONDUX_S3_BUCKET"] = "test-bucket",
        ["CONDUX_S3_ACCESS_KEY"] = "test",
        ["CONDUX_S3_SECRET_KEY"] = "test",
    };

    [Fact]
    public void A_service_that_registers_no_repositories_starts_with_both_passes_on()
    {
        var settings = Storage();
        settings["CONDUX_SYMBOLIZER_URL"] = "http://symbolizer:9100";
        using var provider = Build(settings);

        Assert.NotNull(provider.GetRequiredService<EventSymbolication>());
        Assert.NotNull(provider.GetService<NativeSymbolicator>());
    }

    [Fact]
    public void A_symbolizer_url_that_is_not_a_url_stops_startup_even_with_storage_off()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => Build(new() { ["CONDUX_SYMBOLIZER_URL"] = "symbolizer:9100" }));

        Assert.Contains("CONDUX_SYMBOLIZER_URL", refused.Message);
    }

    // The consumer handles untrusted events and only reads objects, so it is not handed the cleanup seam the
    // deletion queue drains through.
    [Fact]
    public void A_reader_can_read_and_has_no_cleanup()
    {
        using var provider = Build(Storage());

        Assert.NotNull(provider.GetService<IObjectStore>());
        Assert.Null(provider.GetService<IObjectStoreCleanup>());
    }

    // The control-plane drains the deletion queue through the cleanup seam.
    [Fact]
    public void A_writer_keeps_its_cleanup()
    {
        using var provider = Build(Storage(), ObjectStoreAccess.ReadWrite);

        Assert.NotNull(provider.GetService<IObjectStoreCleanup>());
    }
}
