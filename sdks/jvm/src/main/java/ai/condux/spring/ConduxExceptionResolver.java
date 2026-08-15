package ai.condux.spring;

import ai.condux.CaptureContext;
import ai.condux.ConduxClient;
import ai.condux.servlet.ConduxExceptionFilter;
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
        report(request, error);
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

    private void report(HttpServletRequest request, Exception error) {
        try {
            // The filter also reports whatever escapes the servlet. Without this marker an exception no
            // resolver claims would be reported twice, once here and once as it unwinds through the
            // filter, producing two issues for one failure.
            if (request != null) {
                request.setAttribute(ConduxExceptionFilter.REPORTED_ATTRIBUTE, Boolean.TRUE);
            }
            client.captureException(error, false, describe(request));
        } catch (RuntimeException reportingFailure) {
            // Reporting must never become the application's problem, least of all from inside its own
            // error handling.
            reportingFailure.printStackTrace();
        }
    }

    private static CaptureContext describe(HttpServletRequest request) {
        CaptureContext context = CaptureContext.of();
        if (request != null) {
            context.request(request.getRequestURI(), request.getMethod(), request.getQueryString());
        }
        return context;
    }
}
