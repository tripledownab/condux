package ai.condux.spring;

import ai.condux.CaptureContext;
import ai.condux.ConduxClient;
import ai.condux.servlet.ConduxExceptionFilter;
import org.springframework.beans.ConversionNotSupportedException;
import org.springframework.beans.TypeMismatchException;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.ErrorResponse;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.core.Ordered;
import org.springframework.web.servlet.HandlerExceptionResolver;
import org.springframework.web.servlet.ModelAndView;

/**
 * Reports a Spring MVC controller exception to Condux, for the apps a servlet {@link
 * ConduxExceptionFilter} cannot cover.
 *
 * <pre>{@code
 * @Bean
 * ConduxExceptionResolver conduxExceptionResolver(ConduxClient client) {
 *     return new ConduxExceptionResolver(client);
 * }
 * }</pre>
 *
 * <p><b>Why both this and the filter.</b> DispatcherServlet converts an exception into a response only
 * when a HandlerExceptionResolver claims it. Nothing claims an ordinary RuntimeException, so it is
 * rethrown wrapped and the filter sees it. Add a global {@code @ExceptionHandler(Exception.class)},
 * which a great many production applications have, and it is resolved: the filter then sees nothing at
 * all and reports nothing, silently. Verified both ways against a real Spring Boot application.
 *
 * <p>This resolver runs before any of them and <b>never claims the exception</b>: it always returns
 * null, so the application's own handling is completely unaffected and reporting cannot change what a
 * client receives.
 *
 * <p>Depends on spring-web, which is {@code provided}: an application that registers this bean already
 * has Spring on the classpath, and the SDK stays runtime dependency-free for everyone else.
 */
public final class ConduxExceptionResolver implements HandlerExceptionResolver, Ordered {
    private final ConduxClient client;

    public ConduxExceptionResolver(ConduxClient client) {
        if (client == null) {
            // Loud at wiring time rather than from inside a request, matching the filter: a null client
            // discovered per request would throw from the reporting path of an app already failing.
            throw new IllegalArgumentException(
                    "ConduxExceptionResolver requires a ConduxClient. Build one with "
                            + "ConduxClient.builder(dsn).build() and expose it as a @Bean.");
        }
        this.client = client;
    }

    @Override
    public ModelAndView resolveException(
            HttpServletRequest request, HttpServletResponse response, Object handler, Exception error) {
        decide(request, error);
        // Null means "not handled here", so the chain carries on to whatever the application configured.
        // Claiming it would make installing Condux change the application's error responses.
        return null;
    }

    /**
     * Runs before the resolvers that would claim the exception, so it is seen whatever they decide.
     * HIGHEST_PRECEDENCE is safe precisely because this never claims anything.
     */
    @Override
    public int getOrder() {
        return Ordered.HIGHEST_PRECEDENCE;
    }

    private void decide(HttpServletRequest request, Exception error) {
        try {
            // The filter also reports whatever escapes the servlet. Without this marker an exception no
            // resolver claims would be reported twice, once here and once as it unwinds through the
            // filter, producing two issues for one failure.
            //
            // It marks that the decision was MADE, not that an event was sent. Setting it only when we
            // report would leave a caller-caused exception that no resolver claims to be picked up again
            // by the filter, which is this same defect arriving from the other end.
            if (request != null) {
                request.setAttribute(ConduxExceptionFilter.REPORTED_ATTRIBUTE, Boolean.TRUE);
            }
            if (isCallerCaused(error)) {
                return;
            }
            client.captureException(error, false, describe(request));
        } catch (RuntimeException reportingFailure) {
            // Reporting must never become the application's problem, least of all from inside its own
            // error handling.
            reportingFailure.printStackTrace();
        }
    }

    /**
     * True when Spring itself answers this exception with a 4xx, so it describes what the CALLER sent
     * rather than a defect in the application. Filing those lets anyone with network access bury the real
     * ones. See ADR-0044.
     *
     * <p>This resolver runs at HIGHEST_PRECEDENCE and never claims, which is exactly what lets it see an
     * exception a {@code @ControllerAdvice} would otherwise swallow, and exactly what puts it ahead of
     * DefaultHandlerExceptionResolver, whose whole job is this classification. So it has to ask the same
     * question that resolver would, rather than reporting everything in flight.
     *
     * <p><b>"Would DefaultHandlerExceptionResolver claim it" is the WRONG question</b>, which is why this
     * keys on the status instead. That resolver also handles ConversionNotSupportedException,
     * HttpMessageNotWritableException and MethodValidationException, and answers all three with
     * {@code sendServerError}: they are the application's fault and must keep reporting.
     *
     * <p>{@link ErrorResponse} carries the status for most of them, but not all: measured against a real
     * app, HttpMessageNotReadableException and MethodArgumentTypeMismatchException both answer 400 and
     * implement neither. Malformed JSON is the easiest caller-caused case to send in bulk, so leaving it
     * out would look fixed and miss the worst one.
     */
    // Package-private, not private, so the test can call it directly. decide() swallows a RuntimeException
    // by design, since reporting must never become the application's problem, and that makes a crash in
    // here indistinguishable from a decision not to report when tested through resolveException alone.
    // Measured: a planted crash reachable only from an absence test passed 7 checks, 0 failures.
    static boolean isCallerCaused(Exception error) {
        if (error instanceof ErrorResponse response) {
            int status = response.getStatusCode().value();
            return status >= 400 && status < 500;
        }
        // ConversionNotSupportedException EXTENDS TypeMismatchException while being a 500, so it is
        // excluded before the check below, in step with the order DefaultHandlerExceptionResolver uses
        // for the same reason (pinned by sdks/jvm/src/test/java/ai/condux/spring/ConduxExceptionResolverTest.java,
        // which constructs the real exception). Without this, a server-side conversion failure would be
        // silently dropped.
        if (error instanceof ConversionNotSupportedException) {
            return false;
        }
        return error instanceof TypeMismatchException || error instanceof HttpMessageNotReadableException;
    }

    private static CaptureContext describe(HttpServletRequest request) {
        CaptureContext context = CaptureContext.of();
        if (request != null) {
            context.request(request.getRequestURI(), request.getMethod(), request.getQueryString());
        }
        return context;
    }
}
