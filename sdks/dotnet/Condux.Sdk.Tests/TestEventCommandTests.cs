using System.Net;
using System.Text.Json;
using Condux.Sdk.TestEvent;
using Xunit;

namespace Condux.Sdk.Tests;

// The exit code is the contract a CI script branches on, so every test asserts the code, not just the
// output. Delivery runs through the real client and transport stack, since proving that works is the whole
// point of the command.
public class TestEventCommandTests
{
    private const string Dsn = "https://testkey@ingest.example.test/proj-uuid";

    private static Task<int> Run(
        string[] arguments, StringWriter output, StringWriter errorOutput,
        HttpMessageHandler? transport = null, string? environmentDsn = null) =>
        TestEventCommand.RunAsync(arguments, environmentDsn, output, errorOutput, transport);

    [Fact]
    public async Task No_dsn_is_a_usage_error()
    {
        var output = new StringWriter();
        var errorOutput = new StringWriter();

        var code = await Run([], output, errorOutput);

        Assert.Equal(2, code);
        Assert.Contains("CONDUX_DSN", errorOutput.ToString());
    }

    [Fact]
    public async Task A_malformed_dsn_is_a_usage_error()
    {
        var output = new StringWriter();
        var errorOutput = new StringWriter();

        // Keyless: a real Uri, but not a DSN. Nothing is sent, so this is not a delivery failure.
        var code = await Run(["--dsn", "https://ingest.example.test/proj-uuid"], output, errorOutput);

        Assert.Equal(2, code);
        Assert.Contains(TestEventCommand.Usage, errorOutput.ToString());
    }

    [Fact]
    public async Task An_unrecognized_argument_is_a_usage_error()
    {
        var output = new StringWriter();
        var errorOutput = new StringWriter();

        var code = await Run(["--send-everything"], output, errorOutput, environmentDsn: Dsn);

        Assert.Equal(2, code);
        Assert.Contains("--send-everything", errorOutput.ToString());
    }

    [Fact]
    public async Task Delivers_an_info_message_through_the_real_client_and_exits_zero()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        var output = new StringWriter();
        var errorOutput = new StringWriter();

        var code = await Run(["--message", "hello from CI"], output, errorOutput, transport, Dsn);

        Assert.Equal(0, code);
        Assert.Contains("Delivered", output.ToString());
        var body = JsonDocument.Parse(transport.Bodies[^1]).RootElement;
        Assert.Equal("hello from CI", body.GetProperty("message").GetString());
        Assert.Equal("info", body.GetProperty("level").GetString());
        Assert.Equal("condux-test", body.GetProperty("environment").GetString());
        Assert.Equal(
            "https://ingest.example.test/api/proj-uuid/store/", transport.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task An_explicit_dsn_wins_over_the_environment()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        var output = new StringWriter();

        var code = await Run(
            ["--dsn", "https://flagkey@flag.example.test/flag-project"], output, new StringWriter(),
            transport, environmentDsn: Dsn);

        Assert.Equal(0, code);
        Assert.Equal(
            "https://flag.example.test/api/flag-project/store/", transport.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_rejected_event_exits_one_with_the_reason()
    {
        // 400 is not retriable, so this resolves on the first attempt with no backoff to wait on.
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.BadRequest));
        var output = new StringWriter();
        var errorOutput = new StringWriter();

        var code = await Run([], output, errorOutput, transport, Dsn);

        Assert.Equal(1, code);
        Assert.Contains("Delivery FAILED", errorOutput.ToString());
        Assert.Contains("400", errorOutput.ToString());
    }
}
