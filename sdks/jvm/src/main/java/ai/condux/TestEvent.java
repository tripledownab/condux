package ai.condux;

import java.io.PrintStream;

/**
 * {@code condux-test-event} — prove the pipeline end to end.
 *
 * <p>An error monitor's failure mode is silence, and silence looks exactly like health. This sends one
 * info-level message through the real client and transport and reports the delivery outcome, so "did my
 * DSN / network / relay work" is one command instead of waiting for a production error.
 *
 * <pre>{@code
 * java -cp condux.jar ai.condux.TestEvent --dsn https://key@ingest.condux.ai/project
 * CONDUX_DSN=... java -cp condux.jar ai.condux.TestEvent
 * }</pre>
 */
public final class TestEvent {
    static final String USAGE = "Usage: ai.condux.TestEvent [--dsn <dsn>] [--message <text>]";

    private TestEvent() {
    }

    public static void main(String[] args) {
        String dsn = argument(args, "--dsn");
        System.exit(run(dsn != null ? dsn : System.getenv("CONDUX_DSN"),
                argument(args, "--message"), System.out, System.err));
    }

    /** Sends the test event; returns the process exit code (0 delivered, 1 failed, 2 usage). */
    static int run(String dsn, String message, PrintStream out, PrintStream err) {
        if (dsn == null || dsn.isEmpty()) {
            err.println("condux-test-event: no DSN. Pass --dsn <dsn> or set CONDUX_DSN. " + USAGE);
            return 2;
        }

        ConduxClient client;
        try {
            client = ConduxClient.builder(dsn).environment("condux-test").build();
        } catch (IllegalArgumentException malformed) {
            err.println("condux-test-event: " + malformed.getMessage() + ". " + USAGE);
            return 2;
        }

        String text = message != null && !message.isEmpty() ? message : "Condux test event";
        SendResult result = client.captureMessage(text, Level.INFO);
        if (result.ok()) {
            out.println("Delivered \"" + text + "\" (" + result.attempts() + " attempt(s)). Check your "
                    + "project's issues list; a test message appears as an info-level issue.");
            return 0;
        }

        String reason = result.error() != null ? result.error() : "relay answered " + result.status();
        err.println("Delivery FAILED after " + result.attempts() + " attempt(s): " + reason
                + ". Check the DSN (Project settings -> DSN keys) and that the ingest host is reachable.");
        return 1;
    }

    private static String argument(String[] args, String name) {
        for (int i = 0; i < args.length - 1; i++) {
            if (name.equals(args[i])) {
                return args[i + 1];
            }
        }
        return null;
    }
}
