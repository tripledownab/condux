package ai.condux;

import java.util.Map;

/**
 * The HTTP seam. The default implementation posts with {@link java.net.http.HttpClient}; tests inject a
 * scripted one to exercise the retry/backoff with no real network. An implementation throws on a genuine
 * network failure (the retry loop treats a thrown exception as a retriable transport error).
 */
@FunctionalInterface
public interface Transport {
    Response send(String url, Map<String, String> headers, String body) throws Exception;

    /** An HTTP response reduced to what delivery cares about: the status and the (lowercased) headers. */
    record Response(int status, Map<String, String> headers) {
    }
}
