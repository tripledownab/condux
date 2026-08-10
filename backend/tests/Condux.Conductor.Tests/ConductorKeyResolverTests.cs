using System.Security.Cryptography;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.Conductor.Tests;

public class ConductorKeyResolverTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private sealed class FakeReader(StoredLlmConfig? config) : ILlmConfigReader
    {
        public Task<StoredLlmConfig?> GetAsync(long orgId, CancellationToken cancellationToken = default) =>
            Task.FromResult(config);
    }

    [Fact]
    public async Task Uses_the_orgs_byo_key_and_model_when_configured()
    {
        var box = new SecretBox(NewKey());
        var config = new StoredLlmConfig(
            7, "anthropic", "claude-custom", "", box.Seal("org-byo-key"), DateTimeOffset.UtcNow);
        var resolver = new ConductorKeyResolver("platform-default-key", new FakeReader(config), box);

        var resolved = await resolver.ResolveAsync(7, "platform-default-model");

        Assert.Equal("org-byo-key", resolved.ApiKey);
        Assert.Equal("claude-custom", resolved.Model);
    }

    [Fact]
    public async Task Falls_back_to_the_default_when_the_org_has_no_config()
    {
        var resolver = new ConductorKeyResolver(
            "platform-default-key", new FakeReader(null), new SecretBox(NewKey()));

        var resolved = await resolver.ResolveAsync(7, "platform-default-model");

        Assert.Equal("platform-default-key", resolved.ApiKey);
        Assert.Equal("platform-default-model", resolved.Model);
    }

    [Fact]
    public async Task Falls_back_when_the_secret_store_is_not_configured()
    {
        var resolver = new ConductorKeyResolver("platform-default-key", configs: null, box: null);

        var resolved = await resolver.ResolveAsync(7, "platform-default-model");

        Assert.Equal("platform-default-key", resolved.ApiKey);
    }

    [Fact]
    public async Task Uses_the_default_for_an_unattributed_run_org_zero()
    {
        var box = new SecretBox(NewKey());
        var config = new StoredLlmConfig(0, "anthropic", "m", "", box.Seal("k"), DateTimeOffset.UtcNow);
        var resolver = new ConductorKeyResolver("platform-default-key", new FakeReader(config), box);

        var resolved = await resolver.ResolveAsync(0, "platform-default-model");

        Assert.Equal("platform-default-key", resolved.ApiKey);
    }
}
