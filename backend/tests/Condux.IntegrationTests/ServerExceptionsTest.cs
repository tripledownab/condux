using System.Net;
using System.Text;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// What <see cref="ServerExceptions"/> leaves alone: a request that does not bind is the caller's error,
/// and answers 400 as it does in production, rather than reaching the test as an exception. Signup binds
/// its body before it touches a store, so a dummy Postgres string is enough.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ServerExceptionsTest
{
    private const string DummyPostgres = "Host=localhost;Username=x;Password=x;Database=x";

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"email": 1, "password": "test-password-123"}""")]
    public async Task A_body_that_does_not_bind_answers_400(string body)
    {
        var client = ControlPlaneApp.Create(DummyPostgres).CreateClient();

        var response = await client.PostAsync("/api/auth/signup",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
