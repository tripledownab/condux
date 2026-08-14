namespace Condux.Sdk.TestEvent;

/// <summary>
/// <c>condux-test-event</c> — prove the pipeline end to end, because an error monitor's failure mode is
/// silence and silence looks exactly like health. Sends one info-level message through the real client and
/// transport and reports the delivery outcome, so "did my DSN / network / relay work" is one command
/// instead of waiting for a production error.
/// </summary>
public static class TestEventCommand
{
    /// <summary>Printed whenever the arguments do not name a runnable command.</summary>
    public const string Usage = "Usage: condux-test-event [--dsn <dsn>] [--message <text>]";

    private const string DefaultMessage = "Condux test event";

    /// <summary>
    /// Sends the test event and returns the process exit code: 0 delivered, 1 delivery failed, 2 usage
    /// error (no DSN, a malformed DSN, or an unrecognized argument). The exit code is the contract a CI
    /// script branches on.
    /// </summary>
    /// <param name="arguments">The command line arguments.</param>
    /// <param name="environmentDsn">The DSN from <c>CONDUX_DSN</c>, used when <c>--dsn</c> is absent; the
    /// caller owns the environment lookup.</param>
    /// <param name="output">Where the delivery confirmation goes (stdout).</param>
    /// <param name="errorOutput">Where a usage or delivery failure goes (stderr).</param>
    /// <param name="transport">Transport handler for the send. Null uses real HTTP, which is the point of
    /// the command; tests pass a stub.</param>
    public static async Task<int> RunAsync(
        string[] arguments,
        string? environmentDsn,
        TextWriter output,
        TextWriter errorOutput,
        HttpMessageHandler? transport = null)
    {
        string? dsn = null;
        var message = DefaultMessage;

        for (var index = 0; index < arguments.Length; index++)
        {
            var value = index + 1 < arguments.Length ? arguments[index + 1] : null;
            switch (arguments[index])
            {
                case "--dsn" when value is not null:
                    dsn = value;
                    index++;
                    break;
                case "--message" when value is not null:
                    message = value;
                    index++;
                    break;
                default:
                    errorOutput.WriteLine(
                        $"condux-test-event: unrecognized argument '{arguments[index]}'. {Usage}");
                    return 2;
            }
        }

        dsn ??= environmentDsn;
        if (string.IsNullOrWhiteSpace(dsn))
        {
            errorOutput.WriteLine($"condux-test-event: no DSN. Pass --dsn <dsn> or set CONDUX_DSN. {Usage}");
            return 2;
        }

        ConduxClient client;
        try
        {
            client = new ConduxClient(new ConduxOptions
            {
                Dsn = dsn,
                Environment = "condux-test",
                Transport = transport,
            });
        }
        catch (FormatException error)
        {
            // A DSN the client refuses is a usage error, not a delivery failure — nothing was ever sent.
            errorOutput.WriteLine($"condux-test-event: {error.Message} {Usage}");
            return 2;
        }

        var result = await client.CaptureMessageAsync(message, Level.Info);
        if (result.Ok)
        {
            output.WriteLine(
                $"Delivered \"{message}\" ({result.Attempts} attempt(s)). Check your project's issues list; "
                + "a test message appears as an info-level issue.");
            return 0;
        }

        var reason = result.Error ?? $"relay answered {result.Status}";
        errorOutput.WriteLine(
            $"Delivery FAILED after {result.Attempts} attempt(s): {reason}. Check the DSN (Project settings "
            + "-> DSN keys) and that the ingest host is reachable.");
        return 1;
    }
}
