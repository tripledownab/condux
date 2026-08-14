package ai.condux;

import java.util.List;
import java.util.Map;

/**
 * Enrichment-scope tests: the ambient user/tags/contexts/breadcrumbs the relay parses. The wire keys are
 * asserted field by field, because the scope is only useful if it lands in the exact shape the relay's
 * parser reads — and because an unenriched event must keep its current shape exactly (no new keys), which
 * a shape-agnostic test would not catch. Plain-Java harness (a main + assertions), like the other suites.
 */
public final class ConduxScopeTest {
    private static final String DSN = "https://testkey@ingest.test/proj-uuid";
    private static int checks;
    private static int failures;

    public static void main(String[] args) {
        unenrichedEventCarriesNoneOfTheScopeKeys();
        enrichedEventCarriesTheSentryWireShape();
        scopeRidesAnExceptionCaptureToo();
        nullClearsUserTagAndContext();
        breadcrumbTrailIsCappedDroppingTheOldest();
        clearRemovesEverything();

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    private static void unenrichedEventCarriesNoneOfTheScopeKeys() {
        ConduxScope.clear();
        String body = capture();

        for (String key : List.of("\"user\"", "\"tags\"", "\"contexts\"", "\"breadcrumbs\"")) {
            check(!body.contains(key), "an unenriched event carries no " + key);
        }
    }

    private static void enrichedEventCarriesTheSentryWireShape() {
        ConduxScope.clear();
        ConduxScope.setUser(Map.of("id", "42"));
        ConduxScope.setTag("plan", "team");
        ConduxScope.setContext("device", Map.of("model", "laptop"));
        ConduxScope.addBreadcrumb(ConduxScope.Breadcrumb.of("/checkout").category("navigation").level(Level.INFO));

        String body = capture();

        check(body.contains("\"user\":{\"id\":\"42\"}"), "user rides the event");
        check(body.contains("\"tags\":{\"plan\":\"team\"}"), "tags ride the event");
        check(body.contains("\"contexts\":{\"device\":{\"model\":\"laptop\"}}"), "contexts ride the event");
        // Breadcrumbs ride the Sentry {"values": [...]} envelope, not a bare array.
        check(body.contains("\"breadcrumbs\":{\"values\":[{\"message\":\"/checkout\""), "breadcrumbs use the values envelope");
        check(body.contains("\"category\":\"navigation\""), "a breadcrumb keeps its category");
        check(body.contains("\"level\":\"info\""), "a breadcrumb keeps its level");
        check(body.matches("(?s).*\"message\":\"/checkout\",\"timestamp\":[0-9.]+.*"), "a breadcrumb is stamped with epoch seconds");
        ConduxScope.clear();
    }

    private static void scopeRidesAnExceptionCaptureToo() {
        ConduxScope.clear();
        ConduxScope.setTag("plan", "business");

        Recorder recorder = new Recorder(List.of(Scripted.ok(200)));
        build(recorder).captureException(new IllegalStateException("boom"));

        String body = recorder.bodies.get(0);
        check(body.contains("\"tags\":{\"plan\":\"business\"}"), "the scope rides an exception capture");
        check(body.contains("\"exception\""), "the exception still rides the event");
        ConduxScope.clear();
    }

    private static void nullClearsUserTagAndContext() {
        ConduxScope.clear();
        ConduxScope.setUser(Map.of("id", "42"));
        ConduxScope.setTag("plan", "team");
        ConduxScope.setContext("device", Map.of("model", "laptop"));

        ConduxScope.setUser(null);
        ConduxScope.setTag("plan", null);
        ConduxScope.setContext("device", null);
        String body = capture();

        for (String key : List.of("\"user\"", "\"tags\"", "\"contexts\"")) {
            check(!body.contains(key), "null removes " + key);
        }
    }

    private static void breadcrumbTrailIsCappedDroppingTheOldest() {
        ConduxScope.clear();
        for (int index = 0; index < 35; index++) {
            ConduxScope.addBreadcrumb(ConduxScope.Breadcrumb.of("step-" + index));
        }

        String body = capture();

        // Newest last, oldest dropped: steps 0-4 are gone and the trail runs 5..34.
        check(!body.contains("\"step-4\""), "the oldest breadcrumbs are dropped");
        check(body.contains("\"step-5\""), "the trail starts at the oldest kept crumb");
        check(body.contains("\"step-34\""), "the newest breadcrumb is kept");
        check(countOccurrences(body, "\"message\":\"step-") == ConduxScope.MAX_BREADCRUMBS,
                "the trail is capped at MAX_BREADCRUMBS");
        ConduxScope.clear();
    }

    private static void clearRemovesEverything() {
        ConduxScope.setUser(Map.of("id", "42"));
        ConduxScope.setTag("plan", "team");
        ConduxScope.addBreadcrumb(ConduxScope.Breadcrumb.of("/checkout"));

        ConduxScope.clear();
        String body = capture();

        for (String key : List.of("\"user\"", "\"tags\"", "\"contexts\"", "\"breadcrumbs\"")) {
            check(!body.contains(key), "clear() removes " + key);
        }
    }

    /** Sends one message event through a recording transport and returns the JSON that reached the relay. */
    private static String capture() {
        Recorder recorder = new Recorder(List.of(Scripted.ok(200)));
        build(recorder).captureMessage("scope probe", Level.INFO);
        return recorder.bodies.get(0);
    }

    private static ConduxClient build(Recorder recorder) {
        return ConduxClient.builder(DSN).transport(recorder.transport()).sleeper(recorder.sleeper()).build();
    }

    private static int countOccurrences(String haystack, String needle) {
        int count = 0;
        for (int at = haystack.indexOf(needle); at >= 0; at = haystack.indexOf(needle, at + needle.length())) {
            count++;
        }
        return count;
    }

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) {
            failures++;
            System.err.println("FAIL: " + message);
        }
    }
}
