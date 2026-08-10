package ai.condux.servlet;

import ai.condux.ConduxClient;
import jakarta.servlet.Filter;
import jakarta.servlet.FilterChain;
import jakarta.servlet.ServletException;
import jakarta.servlet.ServletRequest;
import jakarta.servlet.ServletResponse;
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
    private final ConduxClient client;

    public ConduxExceptionFilter(ConduxClient client) {
        this.client = client;
    }

    @Override
    public void doFilter(ServletRequest request, ServletResponse response, FilterChain chain)
            throws IOException, ServletException {
        try {
            chain.doFilter(request, response);
        } catch (IOException | ServletException | RuntimeException error) {
            client.captureException(error, false);
            throw error;
        }
    }
}
