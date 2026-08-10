package ai.condux.servlet;

import ai.condux.ConduxClient;
import ai.condux.Transport;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * Proves the Spring Boot / servlet adapter reports an uncaught request exception as unhandled and
 * re-throws, and passes a clean request through untouched. A plain-Java harness (a main + assertions) so
 * CI needs only a JDK + the servlet API on the classpath, matching the base suite.
 */
public final class ConduxExceptionFilterTest {
    private static final String DSN = "https://testkey@ingest.test/proj-uuid";
    private static int checks;
    private static int failures;

    public static void main(String[] args) throws Exception {
        reportsAnUncaughtRequestExceptionAsUnhandledAndRethrows();
        passesACleanRequestThroughWithoutReporting();

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    private static void reportsAnUncaughtRequestExceptionAsUnhandledAndRethrows() throws Exception {
        Recorder recorder = new Recorder();
        ConduxExceptionFilter filter = new ConduxExceptionFilter(client(recorder));
        FilterChain throwing = (request, response) -> {
            throw new IllegalStateException("route boom");
        };

        boolean rethrew = false;
        try {
            filter.doFilter(null, null, throwing);
        } catch (IllegalStateException expected) {
            rethrew = true;
        }

        check(rethrew, "the filter re-throws so the app's own error handling still runs");
        check(recorder.bodies.size() == 1, "the exception is reported once");
        String body = recorder.bodies.get(0);
        check(body.contains("\"value\":\"route boom\""), "the reported value is the exception message");
        check(body.contains("\"handled\":false"), "the mechanism is marked unhandled");
    }

    private static void passesACleanRequestThroughWithoutReporting() throws Exception {
        Recorder recorder = new Recorder();
        ConduxExceptionFilter filter = new ConduxExceptionFilter(client(recorder));
        boolean[] reached = {false};
        FilterChain clean = (request, response) -> reached[0] = true;

        filter.doFilter(null, null, clean);

        check(reached[0], "a clean request reaches the rest of the chain");
        check(recorder.bodies.isEmpty(), "nothing is reported for a clean request");
    }

    private static ConduxClient client(Recorder recorder) {
        return ConduxClient.builder(DSN).transport(recorder).build();
    }

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) {
            failures++;
            System.err.println("FAIL: " + message);
        }
    }

    /** Records the request bodies the SDK sends, and always answers 202 (no real network). */
    private static final class Recorder implements Transport {
        final List<String> bodies = new ArrayList<>();

        @Override
        public Response send(String url, Map<String, String> headers, String body) {
            bodies.add(body);
            return new Response(202, Map.of());
        }
    }
}
