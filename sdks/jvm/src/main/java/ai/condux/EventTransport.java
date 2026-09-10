package ai.condux;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.DoubleConsumer;

/**
 * Delivers a serialized event to the relay, retrying transient failures (429 / 5xx / network) with
 * capped exponential backoff, honoring Retry-After on a 429. Never throws — returns a {@link SendResult}.
 */
final class EventTransport {
    private static final double BASE_BACKOFF_MS = 200.0;
    private static final double MAX_BACKOFF_MS = 30_000.0;

    private final String url;
    private final Map<String, String> headers;
    private final int maxRetries;
    private final Transport transport;
    private final DoubleConsumer sleeper;

    EventTransport(String url, Map<String, String> headers, int maxRetries, Transport transport, DoubleConsumer sleeper) {
        this.url = url;
        this.headers = headers;
        this.maxRetries = maxRetries;
        this.transport = transport;
        this.sleeper = sleeper;
    }

    SendResult send(String body) {
        Integer lastStatus = null;
        String lastError = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++) {
            Integer status = null;
            Map<String, String> responseHeaders = Map.of();
            try {
                Transport.Response response = transport.send(url, headers, body);
                status = response.status();
                responseHeaders = response.headers() == null ? Map.of() : response.headers();
            } catch (Exception e) {
                // A genuine network failure — retry; a failed send must never crash the caller.
                lastError = e.getMessage() != null && !e.getMessage().isEmpty() ? e.getMessage() : e.getClass().getName();
            }

            if (status != null) {
                lastStatus = status;
                lastError = null;
                if (status >= 200 && status < 300) {
                    return new SendResult(true, attempt + 1, status, null);
                }
                if (!retriable(status)) {
                    return new SendResult(false, attempt + 1, status, null);
                }
            }

            if (attempt == maxRetries) {
                break;
            }
            sleeper.accept(backoffSeconds(attempt, status, responseHeaders));
        }

        return new SendResult(false, maxRetries + 1, lastStatus, lastError);
    }

    /** The default transport: a shared {@link HttpClient} that POSTs the event. */
    static Transport httpClient() {
        HttpClient http = HttpClient.newHttpClient();
        return (url, headers, body) -> {
            HttpRequest.Builder request = HttpRequest.newBuilder(URI.create(url))
                .POST(HttpRequest.BodyPublishers.ofString(body));
            headers.forEach(request::header);
            HttpResponse<Void> response = http.send(request.build(), HttpResponse.BodyHandlers.discarding());
            Map<String, String> responseHeaders = new LinkedHashMap<>();
            response.headers().map().forEach((key, values) -> {
                if (!values.isEmpty()) {
                    responseHeaders.put(key.toLowerCase(), values.get(0));
                }
            });
            return new Transport.Response(response.statusCode(), responseHeaders);
        };
    }

    /** The default backoff sleep. */
    static void defaultSleep(double seconds) {
        try {
            Thread.sleep((long) (seconds * 1000));
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    /** 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN / payload) are not. */
    private static boolean retriable(int status) {
        return status == 429 || status >= 500;
    }

    /** Honor Retry-After (seconds) on a 429; otherwise capped exponential backoff. Returns seconds. */
    private static double backoffSeconds(int attempt, Integer status, Map<String, String> headers) {
        double wantedMs = BASE_BACKOFF_MS * Math.pow(2, attempt);
        if (status != null && status == 429) {
            String retryAfter = header(headers, "retry-after");
            if (retryAfter != null && !retryAfter.strip().isEmpty()) {
                try {
                    // parseDouble accepts "Infinity" and "NaN", and neither has a sensible answer
                    // downstream. Not finite is not an instruction, so it falls back to the schedule.
                    double seconds = Double.parseDouble(retryAfter.strip());
                    if (Double.isFinite(seconds)) {
                        wantedMs = Math.max(0.0, seconds) * 1000.0;
                    }
                } catch (NumberFormatException ignored) {
                    // fall through to exponential backoff
                }
            }
        }
        return Math.min(wantedMs, MAX_BACKOFF_MS) / 1000.0;
    }

    private static String header(Map<String, String> headers, String name) {
        for (Map.Entry<String, String> entry : headers.entrySet()) {
            if (entry.getKey().equalsIgnoreCase(name)) {
                return entry.getValue();
            }
        }
        return null;
    }
}
