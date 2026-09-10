using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// POST /api/orgs, the endpoint every account passes through exactly once. It answered 500 in two ways,
/// and Condux files its own 500s against itself, so anyone with an account could put issues in our tracker
/// at will. That is ADR-0044's rule (never file what the caller got wrong as the app's defect) failing on
/// the server side of the same product.
///
/// Both causes were the same shape: a value the database constrained was decided somewhere the database
/// could not see. A non-nullable string on the request record is a compile-time claim, and a slug computed
/// in the browser is not a slug the schema agreed to.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CreateOrgApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<HttpClient> SignedUpClientAsync()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        return client;
    }

    /// <summary>
    /// A body with no name at all. This reached <c>AddWithValue("slug", null)</c> and threw out of Npgsql,
    /// so the answer was 500 rather than a statement about the request.
    /// </summary>
    [Fact]
    public async Task Create_refuses_a_body_with_no_name_rather_than_failing_inside_the_driver()
    {
        var client = await SignedUpClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orgs", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_name", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_refuses_a_blank_name(string name)
    {
        var client = await SignedUpClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orgs", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// The realistic production path. The browser slugified a name with no ASCII letters to the empty
    /// string; the first such org took it and every later one collided with the UNIQUE constraint. Two
    /// orgs named in Japanese must both be creatable, by different users, in that order.
    /// </summary>
    [Fact]
    public async Task Two_orgs_named_without_ascii_letters_are_both_created()
    {
        var first = await (await SignedUpClientAsync()).PostAsJsonAsync("/api/orgs", new { name = "日本語" });
        var second = await (await SignedUpClientAsync()).PostAsJsonAsync("/api/orgs", new { name = "日本語" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Neither slug is blank: the column reads as descriptive, so a row holding "" is a lie about the org.
        foreach (var resp in new[] { first, second })
        {
            var org = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.NotEqual("", org.GetProperty("slug").GetString());
        }
    }

    /// <summary>
    /// Two customers with the same name. The slug is derived from the name and the name is not unique, so
    /// this must work; while the slug was UNIQUE the second customer could not create an org at all, and
    /// had no field in the form to change, since the form asks only for a name.
    /// </summary>
    [Fact]
    public async Task Two_orgs_with_the_same_name_are_both_created_and_share_a_slug()
    {
        var first = await (await SignedUpClientAsync()).PostAsJsonAsync("/api/orgs", new { name = "Acme" });
        var second = await (await SignedUpClientAsync()).PostAsJsonAsync("/api/orgs", new { name = "Acme" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var firstOrg = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondOrg = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("acme", firstOrg.GetProperty("slug").GetString());
        Assert.Equal("acme", secondOrg.GetProperty("slug").GetString());
        Assert.NotEqual(firstOrg.GetProperty("id").GetInt64(), secondOrg.GetProperty("id").GetInt64());
    }

    /// <summary>
    /// A slug in the body is ignored, not honoured. It is the server's value now, so a caller cannot post
    /// one that the server would not have derived.
    /// </summary>
    [Fact]
    public async Task Create_ignores_a_slug_supplied_by_the_caller()
    {
        var client = await SignedUpClientAsync();

        var resp = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme", slug = "chosen-by-caller" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var org = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("acme", org.GetProperty("slug").GetString());
    }

    /// <summary>The ADR-0018 single-org rule still answers before anything else the endpoint does.</summary>
    [Fact]
    public async Task Create_still_refuses_a_second_org_for_the_same_user()
    {
        var client = await SignedUpClientAsync();

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/orgs", new { name = "First" })).StatusCode);

        var second = await client.PostAsJsonAsync("/api/orgs", new { name = "Second" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("single_org_limit", body.GetProperty("error").GetString());
    }
}
