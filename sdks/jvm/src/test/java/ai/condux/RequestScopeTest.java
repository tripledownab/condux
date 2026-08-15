package ai.condux;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CyclicBarrier;

/**
 * Request isolation and per-event enrichment.
 *
 * <p>The concurrency test runs two real threads that both set their user before either captures, so a
 * shared scope makes whichever wrote last win for both events. A sequential test would pass with the bug
 * present and prove nothing.
 */
public final class RequestScopeTest {
    public static void main(String[] args) throws Exception {
        processScopeStillAppliesWithNoRequestActive();
        requestScopeLayersOverProcessScope();
        requestScopeDoesNotOutliveTheRequest();
        concurrentThreadsDoNotSeeEachOthersUser();
        perEventRequestRidesTheWire();
        anEventWithNoContextHasNoRequestField();
        System.out.println("RequestScopeTest OK");
    }

    private static void processScopeStillAppliesWithNoRequestActive() {
        ConduxScope.clear();
        Recorder recorder = recorder();
        ConduxClient client = client(recorder);

        // Backward compatibility: a startup setTag must behave exactly as before request scopes existed.
        ConduxScope.setTag("service", "billing");
        client.captureMessage("hello", Level.INFO);

        Assert.equals("billing", tag(last(recorder), "service"), "process tag");
        ConduxScope.clear();
    }

    private static void requestScopeLayersOverProcessScope() {
        ConduxScope.clear();
        Recorder recorder = recorder();
        ConduxClient client = client(recorder);
        ConduxScope.setTag("service", "billing");

        try (ConduxScope.RequestScope scope = ConduxScope.beginRequest()) {
            ConduxScope.setTag("tenant", "acme");
            client.captureMessage("inside", Level.INFO);
        }

        Assert.equals("billing", tag(last(recorder), "service"), "process tag survives");
        Assert.equals("acme", tag(last(recorder), "tenant"), "request tag applies");
        ConduxScope.clear();
    }

    private static void requestScopeDoesNotOutliveTheRequest() {
        ConduxScope.clear();
        Recorder recorder = recorder();
        ConduxClient client = client(recorder);

        try (ConduxScope.RequestScope scope = ConduxScope.beginRequest()) {
            ConduxScope.setUser(Map.of("id", "u-1"));
        }
        client.captureMessage("after", Level.INFO);

        // The leak in miniature: without the removal on close, the next event on this pooled thread
        // carries the previous request's user.
        Assert.isTrue(!last(recorder).contains("\"user\""), "no user after the scope closed");
        ConduxScope.clear();
    }

    private static void concurrentThreadsDoNotSeeEachOthersUser() throws Exception {
        ConduxScope.clear();
        Recorder recorder = recorder();
        ConduxClient client = client(recorder);
        CyclicBarrier barrier = new CyclicBarrier(2);

        Runnable handle1 = handler(client, barrier, "u-1");
        Runnable handle2 = handler(client, barrier, "u-2");
        Thread first = new Thread(handle1);
        Thread second = new Thread(handle2);
        first.start();
        second.start();
        first.join();
        second.join();

        List<String> bodies = recorder.bodies;
        Assert.equals(2, bodies.size(), "both events sent");
        for (String body : bodies) {
            // Each event's message names the user that captured it, so a crossed scope shows up as a
            // body whose message and user disagree.
            String expected = body.contains("\"message\":\"u-1\"") ? "u-1" : "u-2";
            Assert.isTrue(body.contains("\"id\":\"" + expected + "\""), "user matches its own request");
        }
        ConduxScope.clear();
    }

    private static Runnable handler(ConduxClient client, CyclicBarrier barrier, String userId) {
        return () -> {
            try (ConduxScope.RequestScope scope = ConduxScope.beginRequest()) {
                ConduxScope.setUser(Map.of("id", userId));
                barrier.await(); // both users are set before either captures
                client.captureMessage(userId, Level.INFO);
            } catch (Exception failure) {
                throw new RuntimeException(failure);
            }
        };
    }

    private static void perEventRequestRidesTheWire() {
        ConduxScope.clear();
        Recorder recorder = recorder();
        ConduxClient client = client(recorder);

        client.captureException(new IllegalStateException("boom"), false,
                CaptureContext.of().request("/checkout", "POST", "step=2").tag("route", "/checkout/{id}"));

        String body = last(recorder);
        Assert.isTrue(body.contains("\"url\":\"/checkout\""), "url");
        Assert.isTrue(body.contains("\"method\":\"POST\""), "method");
        // snake_case, because that is the key the relay's parser reads.
        Assert.isTrue(body.contains("\"query_string\":\"step=2\""), "query_string");
        Assert.isTrue(body.contains("\"route\":\"/checkout/{id}\""), "per-event tag");
        ConduxScope.clear();
    }

    private static void anEventWithNoContextHasNoRequestField() {
        ConduxScope.clear();
        Recorder recorder = recorder();
        client(recorder).captureMessage("plain", Level.INFO);

        // Absence, not an empty object: an unenriched event must keep its exact previous wire shape.
        Assert.isTrue(!last(recorder).contains("\"request\""), "no request field");
        ConduxScope.clear();
    }

    private static Recorder recorder() {
        return new Recorder(List.of(Scripted.ok(202)));
    }

    private static ConduxClient client(Recorder recorder) {
        return ConduxClient.builder("http://pub123@relay.test/7").transport(recorder.transport()).build();
    }

    private static String last(Recorder recorder) {
        return recorder.bodies.get(recorder.bodies.size() - 1);
    }

    private static String tag(String body, String key) {
        Map<String, String> tags = new LinkedHashMap<>();
        int start = body.indexOf("\"tags\":{");
        if (start < 0) {
            return null;
        }
        String section = body.substring(start + "\"tags\":{".length(), body.indexOf('}', start));
        for (String pair : section.split(",")) {
            String[] parts = pair.split(":", 2);
            tags.put(parts[0].replace("\"", ""), parts[1].replace("\"", ""));
        }
        return tags.get(key);
    }

    private static final class Assert {
        static void equals(Object expected, Object actual, String what) {
            if (expected == null ? actual != null : !expected.equals(actual)) {
                throw new AssertionError(what + ": expected " + expected + " but was " + actual);
            }
        }

        static void isTrue(boolean condition, String what) {
            if (!condition) {
                throw new AssertionError(what);
            }
        }
    }
}
