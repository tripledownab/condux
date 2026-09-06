using Condux.ControlPlane.Setup;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.ControlPlane.Tests;

public class IngestDsnTests
{
    private static readonly Guid PublicId = Guid.Parse("018f5b4e-0000-7000-8000-000000000001");

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Uses_the_configured_ingest_scheme_and_host()
    {
        var dsn = IngestDsn.Build(
            Config(("CONDUX_INGEST_SCHEME", "https"), ("CONDUX_INGEST_HOST", "ingest.condux.ai")),
            "pubkey", PublicId);

        Assert.Equal($"https://pubkey@ingest.condux.ai/{PublicId}", dsn);
    }

    [Fact]
    public void Falls_back_to_the_dev_localhost_relay_when_unset()
    {
        var dsn = IngestDsn.Build(Config(), "pubkey", PublicId);

        Assert.Equal($"http://pubkey@localhost:9010/{PublicId}", dsn);
    }
}
