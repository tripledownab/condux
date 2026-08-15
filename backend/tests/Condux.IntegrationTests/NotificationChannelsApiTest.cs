using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Org notification channels API (#129) over HTTP, tenancy enforced: add / list / delete a channel,
/// reject an unknown channel type, and 404 a delete of a missing one. These back the Conductor-pause
/// notices (#130).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NotificationChannelsApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<(HttpClient Client, long OrgId)> ProvisionAsync()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 1);
        return (client, orgId);
    }

    [Fact]
    public async Task Add_List_Delete_RoundTrips()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();

        var add = await client.PostAsJsonAsync($"/api/orgs/{orgId}/notification-channels",
            new { channel = 3, target = "https://hooks.test/w" }); // 3 = webhook
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/notification-channels");
        var channel = Assert.Single(list.EnumerateArray());
        Assert.Equal(3, channel.GetProperty("channel").GetInt32());
        Assert.Equal("https://hooks.test/w", channel.GetProperty("target").GetString());

        var del = await client.DeleteAsync($"/api/orgs/{orgId}/notification-channels/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Empty(
            (await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/notification-channels")).EnumerateArray());

        // Deleting an already-gone channel 404s.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/orgs/{orgId}/notification-channels/{id}")).StatusCode);
    }

    [Fact]
    public async Task Add_RejectsAnUnknownChannelOrEmptyTarget()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/orgs/{orgId}/notification-channels",
                new { channel = 99, target = "https://hooks.test/w" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/orgs/{orgId}/notification-channels",
                new { channel = 3, target = "" })).StatusCode);
    }

    [Fact]
    public async Task TestSend_UnconfiguredEmailChannel_ReportsNotConfigured()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();

        // Email (channel = 1) has no notifier in a host without CONDUX_SMTP_HOST, so the test send reports
        // channel_not_configured rather than a 500 (and stays network-free).
        var add = await client.PostAsJsonAsync($"/api/orgs/{orgId}/notification-channels",
            new { channel = 1, target = "ops@acme.test" });
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var test = await client.PostAsync($"/api/orgs/{orgId}/notification-channels/{id}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var body = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("delivered").GetBoolean());
        Assert.Equal("channel_not_configured", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task TestSend_MissingChannel_404s()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();

        var test = await client.PostAsync(
            $"/api/orgs/{orgId}/notification-channels/{Guid.NewGuid()}/test", null);
        Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
    }
}
