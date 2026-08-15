package ai.condux;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Per-event enrichment, passed at the capture call rather than set ambiently: the request an error
 * happened during, and tags scoped to that one event.
 *
 * <p>This exists because {@link ConduxScope}'s process state outlives a single call. A servlet container
 * serves requests concurrently, so request detail set ambiently attaches to whichever event is captured
 * next, which may belong to a different request. A wrong URL is worse than none, because it sends
 * whoever is debugging to the wrong endpoint.
 *
 * <p>Deliberately no headers. They carry Cookie and Authorization, and while the relay scrubs sensitive
 * keys at ingest, not sending credentials at all is the stronger guarantee.
 *
 * <pre>{@code
 * client.captureException(error, false, CaptureContext.of()
 *         .request("/checkout", "POST", "step=2")
 *         .tag("route", "/checkout/{id}"));
 * }</pre>
 */
public final class CaptureContext {
    private final Map<String, String> request = new LinkedHashMap<>();
    private final Map<String, String> tags = new LinkedHashMap<>();

    private CaptureContext() {}

    /** Start building. */
    public static CaptureContext of() {
        return new CaptureContext();
    }

    /**
     * The request this event happened during. Null or blank parts are omitted, so a partially known
     * request does not ship empty strings the relay would have to interpret.
     *
     * @param url the path or absolute URL, without the query string
     * @param method the HTTP method
     * @param queryString the query string, without a leading "?"
     */
    public CaptureContext request(String url, String method, String queryString) {
        put(request, "url", url);
        put(request, "method", method);
        // The wire field is snake_case: it is the key the relay's parser reads.
        put(request, "query_string", queryString);
        return this;
    }

    /** A tag for this event only, merged over the ambient ones. */
    public CaptureContext tag(String key, String value) {
        put(tags, key, value);
        return this;
    }

    Map<String, String> request() {
        return request;
    }

    Map<String, String> tags() {
        return tags;
    }

    private static void put(Map<String, String> target, String key, String value) {
        if (value != null && !value.isEmpty()) {
            target.put(key, value);
        }
    }
}
