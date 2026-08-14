package ai.condux;

import java.util.List;
import java.util.Map;
import java.util.regex.Pattern;

/**
 * Proves the emitted JSON is the Sentry store wire shape (field by field) and the transport is resilient,
 * with an injected transport + sleep so there is no real network or waiting. Mirrors the JS/Python/Go/
 * Ruby/PHP suites. A plain-Java harness (a main + assertions) so CI needs only a JDK — no JUnit, no build
 * tool. Assertions are substring/regex checks on the raw JSON, since the SDK is dependency-free (no JSON
 * parser to read it back).
 */
public final class ConduxClientTest {
    private static final String DSN = "https://testkey@ingest.test/proj-uuid";
    private static int checks;
    private static int failures;

    public static void main(String[] args) {
        captureExceptionEmitsSentryStoreShape();
        captureExceptionCanMarkAnExceptionUnhandled();
        captureMessageEmitsMessageWithoutException();
        retries429HonoringRetryAfter();
        retries5xxWithCappedExponentialBackoff();
        doesNotRetryAClientError();
        reportsANetworkErrorWithoutThrowing();
        rejectsAMalformedDsn();

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    private static void captureExceptionEmitsSentryStoreShape() {
        Recorder recorder = new Recorder(List.of(Scripted.ok(200)));
        ConduxClient client = build(recorder);
        SendResult result;
        try {
            throw new IllegalArgumentException("boom from java");
        } catch (IllegalArgumentException e) {
            result = client.captureException(e);
        }

        check(result.ok(), "captureException reports ok on a 200");
        String body = recorder.bodies.get(0);
        check(Pattern.compile("\"event_id\":\"[0-9a-f]{32}\"").matcher(body).find(), "event_id is 32 lowercase hex");
        check(body.contains("\"platform\":\"java\""), "platform is java");
        check(body.contains("\"level\":\"error\""), "captureException level is error");
        check(body.contains("\"environment\":\"test\""), "environment rides the event");
        check(body.contains("\"release\":\"1.2.3\""), "release rides the event");
        check(body.contains("\"type\":\"java.lang.IllegalArgumentException\""), "exception type is the class");
        check(body.contains("\"value\":\"boom from java\""), "exception value is the message");
        check(body.contains("\"mechanism\":{\"type\":\"generic\",\"handled\":true}"), "mechanism is generic + handled");
        check(body.contains("\"stacktrace\""), "a stack trace is attached");
        check(body.contains("ConduxClientTest"), "the throwing method is in the trace");
        check(body.contains("\"in_app\":true"), "the app frame is in-app");
        check("testkey".equals(recorder.requests.get(0).get("x-condux-auth")), "auth header carries the DSN key");
        check("https://ingest.test/api/proj-uuid/store/".equals(recorder.urls.get(0)), "store URL is built from the DSN");
    }

    private static void captureExceptionCanMarkAnExceptionUnhandled() {
        Recorder recorder = new Recorder(List.of(Scripted.ok(200)));
        ConduxClient client = build(recorder);
        try {
            throw new IllegalStateException("unhandled boom");
        } catch (IllegalStateException e) {
            client.captureException(e, false);
        }

        String body = recorder.bodies.get(0);
        check(body.contains("\"mechanism\":{\"type\":\"generic\",\"handled\":false}"),
            "captureException(error, false) marks the mechanism unhandled");
    }

    private static void captureMessageEmitsMessageWithoutException() {
        Recorder recorder = new Recorder(List.of(Scripted.ok(200)));
        build(recorder).captureMessage("disk almost full", Level.WARNING);

        String body = recorder.bodies.get(0);
        check(body.contains("\"level\":\"warning\""), "captureMessage carries the level");
        check(body.contains("\"message\":\"disk almost full\""), "captureMessage carries the message");
        check(!body.contains("\"exception\""), "captureMessage has no exception");
    }

    private static void retries429HonoringRetryAfter() {
        Recorder recorder = new Recorder(List.of(Scripted.status(429, Map.of("Retry-After", "3")), Scripted.ok(200)));
        SendResult result = build(recorder).captureMessage("hi", Level.INFO);

        check(result.ok(), "429 then 200 succeeds");
        check(result.attempts() == 2, "429 retry took two attempts");
        check(recorder.delays.equals(List.of(3.0)), "429 honored Retry-After of 3s");
    }

    private static void retries5xxWithCappedExponentialBackoff() {
        Recorder recorder = new Recorder(List.of(Scripted.ok(503), Scripted.ok(503), Scripted.ok(200)));
        SendResult result = build(recorder).captureMessage("hi", Level.INFO);

        check(result.ok(), "5xx eventually succeeds");
        check(result.attempts() == 3, "5xx retried to the third attempt");
        check(recorder.delays.equals(List.of(0.2, 0.4)), "5xx backoff is 200ms then 400ms");
    }

    private static void doesNotRetryAClientError() {
        Recorder recorder = new Recorder(List.of(Scripted.ok(400)));
        SendResult result = build(recorder).captureMessage("hi", Level.INFO);

        check(!result.ok(), "400 is not ok");
        check(result.attempts() == 1, "400 is not retried");
        check(result.status() != null && result.status() == 400, "400 status is reported");
        check(recorder.delays.isEmpty(), "400 caused no backoff");
    }

    private static void reportsANetworkErrorWithoutThrowing() {
        Recorder recorder = new Recorder(List.of(Scripted.error("connection refused")));
        ConduxClient client = ConduxClient.builder(DSN)
            .maxRetries(1)
            .transport(recorder.transport())
            .sleeper(recorder.sleeper())
            .clock(() -> 1_700_000_000.5)
            .build();
        SendResult result = client.captureMessage("hi", Level.INFO);

        check(!result.ok(), "a network failure is not ok");
        check(result.attempts() == 2, "a network failure exhausts the retries");
        check(result.status() == null, "a network failure has no status");
        check(result.error() != null, "a network failure reports the error");
    }

    private static void rejectsAMalformedDsn() {
        boolean threw = false;
        try {
            ConduxClient.builder("https://ingest.test/no-key").build();
        } catch (IllegalArgumentException e) {
            threw = true;
        }
        check(threw, "a keyless DSN is rejected");
    }

    private static ConduxClient build(Recorder recorder) {
        return ConduxClient.builder(DSN)
            .environment("test")
            .release("1.2.3")
            .maxRetries(3)
            .transport(recorder.transport())
            .sleeper(recorder.sleeper())
            .clock(() -> 1_700_000_000.5)
            .build();
    }

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) {
            failures++;
            System.err.println("FAIL: " + message);
        }
    }

}
