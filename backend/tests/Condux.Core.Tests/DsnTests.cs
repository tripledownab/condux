using Condux.Core.Plans;
using Condux.Core.Projects;
using Xunit;

namespace Condux.Core.Tests;

public class DsnTests
{
    [Fact]
    public void ParsesValidDsn()
    {
        Assert.True(Dsn.TryParse("https://pub123@relay.condux.ai/42", out var dsn));
        Assert.Equal("pub123", dsn!.PublicKey);
        Assert.Equal("relay.condux.ai", dsn.Host);
        Assert.Equal("42", dsn.ProjectId);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://relay.condux.ai/42")] // no public key
    [InlineData("https://pub@relay.condux.ai/")] // no project id
    public void RejectsInvalidDsn(string dsn) => Assert.False(Dsn.TryParse(dsn, out _));

    [Fact]
    public async Task ProjectStoreAuthenticatesByDsn()
    {
        var store = new InMemoryProjectStore([("42", "pub123", Tier.Team)]);

        var project = await store.AuthenticateAsync("42", "pub123");
        Assert.NotNull(project);
        Assert.Equal("42", project!.Id);
        Assert.Equal(Tier.Team, project.Tier);

        Assert.Null(await store.AuthenticateAsync("42", "wrongkey")); // wrong key
        Assert.Null(await store.AuthenticateAsync("999", "pub123"));  // unknown project
    }
}
