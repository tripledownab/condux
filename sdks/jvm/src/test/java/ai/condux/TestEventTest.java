package ai.condux;

import com.sun.net.httpserver.HttpServer;
import java.io.ByteArrayOutputStream;
import java.io.PrintStream;
import java.net.InetSocketAddress;

/**
 * Tests for {@code ai.condux.TestEvent}. The exit code is the contract a CI script branches on, so assert
 * the code, not just the output. Delivery runs against a real local HTTP server (the JDK's own
 * {@link HttpServer}, so no dependency is added): the command's whole purpose is proving the actual
 * transport works, and a stub transport here would test nothing the other suites do not.
 */
public final class TestEventTest {
    private static int checks;
    private static int failures;

    public static void main(String[] args) throws Exception {
        noDsnIsAUsageError();
        malformedDsnIsAUsageError();
        deliveryToALiveRelayExitsZero();
        aRefusedRelayExitsOne();

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    private static void noDsnIsAUsageError() {
        Output output = new Output();
        check(TestEvent.run(null, null, output.out(), output.err()) == 2, "a missing DSN exits 2");
        check(output.err.toString().contains("no DSN"), "a missing DSN explains itself");
    }

    private static void malformedDsnIsAUsageError() {
        Output output = new Output();
        check(TestEvent.run("https://ingest.test/no-key", null, output.out(), output.err()) == 2,
                "a malformed DSN exits 2");
    }

    private static void deliveryToALiveRelayExitsZero() throws Exception {
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        StringBuilder path = new StringBuilder();
        server.createContext("/", exchange -> {
            path.append(exchange.getRequestURI().getPath());
            exchange.sendResponseHeaders(200, -1);
            exchange.close();
        });
        server.start();
        try {
            Output output = new Output();
            int code = TestEvent.run("http://key@127.0.0.1:" + server.getAddress().getPort() + "/1",
                    "hello", output.out(), output.err());

            check(code == 0, "a delivered event exits 0");
            check(output.out.toString().contains("Delivered"), "a delivered event says so");
            check("/api/1/store/".contentEquals(path), "the event is POSTed to the store endpoint");
        } finally {
            server.stop(0);
        }
    }

    private static void aRefusedRelayExitsOne() {
        // Port 9 (discard) refuses immediately, so the retry loop gives up without waiting on a timeout.
        Output output = new Output();
        check(TestEvent.run("http://key@127.0.0.1:9/1", null, output.out(), output.err()) == 1,
                "a failed delivery exits 1");
        check(output.err.toString().contains("Delivery FAILED"), "a failed delivery says why");
    }

    /**
     * Captures the command's two streams so the assertions can read what a user would see. The streams
     * auto-flush, otherwise PrintStream's internal buffer would leave the assertions reading nothing.
     */
    private static final class Output {
        final ByteArrayOutputStream out = new ByteArrayOutputStream();
        final ByteArrayOutputStream err = new ByteArrayOutputStream();
        private final PrintStream outStream = new PrintStream(out, true);
        private final PrintStream errStream = new PrintStream(err, true);

        PrintStream out() {
            return outStream;
        }

        PrintStream err() {
            return errStream;
        }
    }

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) {
            failures++;
            System.err.println("FAIL: " + message);
        }
    }
}
