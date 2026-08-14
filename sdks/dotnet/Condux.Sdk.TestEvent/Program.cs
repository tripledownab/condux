namespace Condux.Sdk.TestEvent;

// The entry point stays thin: it resolves the environment DSN and hands everything else to
// TestEventCommand, which is what the tests drive.
//
//   condux-test-event --dsn "https://<key>@ingest.condux.ai/<projectId>"
//   CONDUX_DSN="https://<key>@ingest.condux.ai/<projectId>" condux-test-event
internal static class Program
{
    private static Task<int> Main(string[] arguments) =>
        TestEventCommand.RunAsync(
            arguments, Environment.GetEnvironmentVariable("CONDUX_DSN"), Console.Out, Console.Error);
}
