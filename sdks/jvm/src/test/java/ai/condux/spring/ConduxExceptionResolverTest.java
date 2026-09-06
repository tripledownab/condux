package ai.condux.spring;

import ai.condux.ConduxClient;
import ai.condux.Transport;
import ai.condux.servlet.ConduxExceptionFilter;
import jakarta.servlet.http.HttpServletRequest;
import java.lang.reflect.Proxy;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import org.springframework.beans.ConversionNotSupportedException;
import org.springframework.beans.TypeMismatchException;
import org.springframework.http.HttpStatus;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.ErrorResponseException;

/**
 * ADR-0044: the resolver reports an application fault and stays quiet for what the CALLER got wrong.
 *
 * <p>The resolver runs at HIGHEST_PRECEDENCE and never claims, which is what lets it see an exception a
 * {@code @ControllerAdvice} would swallow, and equally what puts it ahead of
 * DefaultHandlerExceptionResolver, whose job is this classification. Measured against a real Spring Boot
 * 3.5.6 app, the filter alone reported 0 of 7 caller-caused requests and the resolver alone reported 7
 * of 7, so the position is the cause and this rule is the fix.
 *
 * <p>A plain-Java harness (a main plus assertions), matching the rest of the suite, so CI needs only a
 * JDK plus the servlet and Spring jars it already downloads.
 */
public final class ConduxExceptionResolverTest {
    private static final String DSN = "https://testkey@ingest.test/proj-uuid";
    private static int checks;
    private static int failures;

    public static void main(String[] args) {
        anApplicationFaultIsReported();
        aFourHundredIsNotReported();
        aFiveHundredErrorResponseIsStillReported();
        malformedBodyAndTypeMismatchAreNotReported();
        aServerSideConversionFailureIsStillReported();
        theDecisionIsMarkedEvenWhenNothingIsReported();
        theClassifierIsCalledDirectlySoACrashCannotHide();

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    /** The control, and first: every absence below would also pass against a recorder that never records. */
    private static void anApplicationFaultIsReported() {
        Recorder recorder = new Recorder();
        resolve(recorder, new IllegalStateException("route boom"));

        check("an application fault is reported", recorder.bodies.size() == 1);
    }

    private static void aFourHundredIsNotReported() {
        Recorder recorder = new Recorder();
        resolve(recorder, new ErrorResponseException(HttpStatus.BAD_REQUEST));

        check("a 4xx ErrorResponse is not reported", recorder.bodies.isEmpty());
    }

    private static void aFiveHundredErrorResponseIsStillReported() {
        Recorder recorder = new Recorder();
        resolve(recorder, new ErrorResponseException(HttpStatus.SERVICE_UNAVAILABLE));

        // The rule is a 4xx RANGE, not "carries a status". A 503 is the application's problem.
        check("a 5xx ErrorResponse is still reported", recorder.bodies.size() == 1);
    }

    private static void malformedBodyAndTypeMismatchAreNotReported() {
        Recorder body = new Recorder();
        resolve(body, new HttpMessageNotReadableException("malformed json", null, null));
        check("a malformed body is not reported", body.bodies.isEmpty());

        Recorder mismatch = new Recorder();
        resolve(mismatch, new TypeMismatchException("not-a-number", Integer.class));
        // Neither type implements ErrorResponse, measured, yet Spring answers both with 400. Without
        // these two the fix would look complete and still file every malformed JSON body.
        check("a type mismatch is not reported", mismatch.bodies.isEmpty());
    }

    private static void aServerSideConversionFailureIsStillReported() {
        Recorder recorder = new Recorder();
        resolve(recorder, new ConversionNotSupportedException("value", Integer.class, null));

        // The trap this test exists for. ConversionNotSupportedException EXTENDS TypeMismatchException,
        // so the check above would swallow it, but Spring answers it with sendServerError: the
        // application cannot convert a value it produced, which is its own defect. Spring orders its own
        // checks the same way for the same reason.
        check("a server-side conversion failure is still reported", recorder.bodies.size() == 1);
    }

    private static void theDecisionIsMarkedEvenWhenNothingIsReported() {
        Recorder recorder = new Recorder();
        Map<String, Object> attributes = new HashMap<>();
        resolver(recorder).resolveException(
                request(attributes), null, null, new ErrorResponseException(HttpStatus.BAD_REQUEST));

        // The filter reports whatever escapes the servlet. If the resolver decided to stay quiet without
        // recording that, the filter would report it anyway and the fix would do nothing.
        check("the decision is marked even when nothing is reported",
                Boolean.TRUE.equals(attributes.get(ConduxExceptionFilter.REPORTED_ATTRIBUTE)));
    }

    /**
     * The classifier called directly, because every other check here goes through resolveException, which
     * swallows a RuntimeException so that reporting can never become the application's problem. That is
     * correct behaviour and it makes a crash in the classifier look exactly like a decision not to report:
     * measured, a planted crash reachable only from an absence test passed 7 checks with 0 failures. These
     * calls let it escape to the runner instead.
     */
    private static void theClassifierIsCalledDirectlySoACrashCannotHide() {
        check("a 4xx ErrorResponse classifies as the caller's",
                ConduxExceptionResolver.isCallerCaused(new ErrorResponseException(HttpStatus.BAD_REQUEST)));
        check("a malformed body classifies as the caller's",
                ConduxExceptionResolver.isCallerCaused(
                        new HttpMessageNotReadableException("malformed json", null, null)));
        check("a 5xx ErrorResponse does not",
                !ConduxExceptionResolver.isCallerCaused(
                        new ErrorResponseException(HttpStatus.SERVICE_UNAVAILABLE)));
        check("a server-side conversion failure does not",
                !ConduxExceptionResolver.isCallerCaused(
                        new ConversionNotSupportedException("value", Integer.class, null)));
    }

    private static void resolve(Recorder recorder, Exception error) {
        resolver(recorder).resolveException(request(new HashMap<>()), null, null, error);
    }

    private static ConduxExceptionResolver resolver(Recorder recorder) {
        return new ConduxExceptionResolver(
                ConduxClient.builder(DSN).transport(recorder).maxRetries(0).build());
    }

    /** Enough HttpServletRequest to record the marker, without a servlet container. */
    private static HttpServletRequest request(Map<String, Object> attributes) {
        return (HttpServletRequest) Proxy.newProxyInstance(
                ConduxExceptionResolverTest.class.getClassLoader(),
                new Class<?>[] {HttpServletRequest.class},
                (proxy, method, args) -> {
                    if (method.getName().equals("setAttribute")) {
                        attributes.put((String) args[0], args[1]);
                        return null;
                    }
                    return method.getReturnType().isPrimitive() ? 0 : null;
                });
    }

    private static void check(String message, boolean condition) {
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
