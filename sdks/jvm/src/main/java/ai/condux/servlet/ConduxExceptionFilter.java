package ai.condux.servlet;

import ai.condux.CaptureContext;
import ai.condux.ConduxClient;
import ai.condux.ConduxScope;
import jakarta.servlet.Filter;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.ServletRequest;
import jakarta.servlet.ServletResponse;
import jakarta.servlet.http.HttpServletRequest;
import java.io.IOException;

/**
 * A servlet {@link Filter} that reports an uncaught request exception to Condux (as unhandled) and
 * re-throws, so the app's own error handling still runs. Works in Spring Boot MVC (register it as a bean)
 * and any Jakarta servlet app. Depends only on the servlet API, which the container provides at runtime.
 *
 * <pre>{@code
 * @Bean
 * FilterRegistrationBean<ConduxExceptionFilter> conduxFilter(ConduxClient client) {
 *     var registration = new FilterRegistrationBean<>(new ConduxExceptionFilter(client));
 *     registration.setOrder(Ordered.HIGHEST_PRECEDENCE); // see the exception before the app handles it
 *     return registration;
 * }
 * }</pre>
 */
public final class ConduxExceptionFilter implements Filter {
    /**
     * Set on the request once an exception has been reported, so it is not reported again as it unwinds.
     *
     * <p>In a Spring app both this filter and ConduxExceptionResolver are registered: the resolver
     * covers exceptions a @ControllerAdvice claims, the filter covers what escapes the servlet. An
     * exception nothing claims passes through both, so without this marker one failure becomes two
     * issues. Public because the resolver lives in another package and must set the same key.
     */
    public static final String REPORTED_ATTRIBUTE = "ai.condux.reported";

    private final ConduxClient client;

    public ConduxExceptionFilter(ConduxClient client) {
        if (client == null) {
            // Loud at registration (developer time), never on a request. A null client here would throw
            // from inside the catch below, replacing every request's real exception with a
            // NullPointerException from the monitoring layer.
            throw new IllegalArgumentException(
                    "ConduxExceptionFilter requires a ConduxClient. Build one with "
                            + "ConduxClient.builder(dsn).build() and register it (for Spring, expose it as a @Bean).");
        }
        this.client = client;
    }

    @Override
    public void doFilter(ServletRequest request, ServletResponse response, FilterChain chain)
            throws IOException, ServletException {
        // A scope per request, so a setUser in a controller belongs to that request and cannot attach to
        // a concurrent one. Containers pool request threads, which is exactly why this has to be scoped
        // rather than left to process state, and why the scope removes itself on the way out.
        try (ConduxScope.RequestScope scope = ConduxScope.beginRequest()) {
            chain.doFilter(request, response);
        } catch (IOException | ServletException | RuntimeException error) {
            report(error, request);
            throw error;
        }
    }

    // The application's exception must reach its own error handling whatever the monitoring layer does, so
    // reporting can never be what propagates out of this filter.
    private void report(Throwable error, ServletRequest request) {
        try {
            if (request != null && request.getAttribute(REPORTED_ATTRIBUTE) != null) {
                return; // already reported by ConduxExceptionResolver on the way out of the servlet
            }
            client.captureException(unwrap(error), false, describe(request));
        } catch (RuntimeException reportingFailure) {
            reportingFailure.printStackTrace();
        }
    }

    /**
     * The application's exception, not the container's wrapper around it.
     *
     * <p>A servlet container rethrows what a handler threw wrapped in a {@link ServletException}, so what
     * arrives here reads as {@code jakarta.servlet.ServletException: Request processing failed:
     * java.lang.IllegalStateException: ...}. Grouping keys on the exception type, so reporting the
     * wrapper collapses every error in an application into a single issue and buries the real type in a
     * message. Verified against a real Spring Boot app, where every controller failure arrived as
     * ServletException.
     *
     * <p>Only the servlet wrapper is unwrapped, and only one level, because an application's own
     * exception chain is meaningful and its outermost type is the one it meant to throw.
     */
    private static Throwable unwrap(Throwable error) {
        if (error instanceof ServletException && error.getCause() != null) {
            return error.getCause();
        }
        return error;
    }

    /**
     * The request, in the shape the relay parses. Reads only what the plain servlet API offers, so this
     * stays framework neutral; headers are available and deliberately not read, since they carry Cookie
     * and Authorization and not sending credentials is the stronger guarantee.
     */
    private static CaptureContext describe(ServletRequest request) {
        CaptureContext context = CaptureContext.of();
        if (request instanceof HttpServletRequest http) {
            context.request(http.getRequestURI(), http.getMethod(), http.getQueryString());
        }
        return context;
    }
}
