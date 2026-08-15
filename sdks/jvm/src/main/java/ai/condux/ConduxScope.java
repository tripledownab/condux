package ai.condux;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
 * leading up to an error.
 *
 * <p>Set once (or as the app's state changes) and every subsequent event carries it — the first triage
 * questions ("which customer, which plan, what did they do last") answered without threading anything
 * through capture calls. The relay already scrubs all of these at ingest and derives the pseudonymous
 * users-affected key from the user fields.
 *
 * <p>The scope is process wide, like every other Condux SDK, so it applies whichever client reports, and
 * synchronized because a JVM app captures from many threads.
 *
 * <pre>{@code
 * ConduxScope.setUser(Map.of("id", "1042", "email", "dev@example.com")); // null clears
 * ConduxScope.setTag("plan", "team");                                    // null removes
 * ConduxScope.addBreadcrumb(Breadcrumb.of("charge.started").category("billing"));
 * }</pre>
 */
public final class ConduxScope {
    /** The trail length kept: newest wins, so a long-lived process drops the oldest crumbs. */
    public static final int MAX_BREADCRUMBS = 30;

    private static final Object LOCK = new Object();

    // A servlet container serves each request on its own thread and returns that thread to a pool, so a
    // ThreadLocal isolates requests AND has to be removed on the way out: state left behind is handed to
    // the next request that worker picks up, which is the exact leak this exists to prevent.
    //
    // Null means no request is in flight, so writes fall through to the process state below. That
    // fallback keeps a startup-time setTag working exactly as it did before request scopes existed.
    private static final ThreadLocal<RequestState> CURRENT = new ThreadLocal<>();

    // Reachable only from its own request's thread, so it needs no lock of its own. The process state
    // below is shared and keeps LOCK.
    private static final class RequestState {
        private Map<String, String> user;
        private final Map<String, String> tags = new LinkedHashMap<>();
        private final Map<String, Map<String, Object>> contexts = new LinkedHashMap<>();
        private final Deque<Map<String, Object>> breadcrumbs = new ArrayDeque<>();
    }

    /**
     * Isolate enrichment to one request: anything set inside is visible only to events captured inside.
     * Close it to end the scope, ideally with try-with-resources. The servlet filter wraps every request
     * in this; call it directly around a background job, which has the same problem of many in flight.
     *
     * <pre>{@code
     * try (var scope = ConduxScope.beginRequest()) {
     *     ConduxScope.setUser(Map.of("id", userId)); // this request only
     *     handle(request);
     * }
     * }</pre>
     */
    public static RequestScope beginRequest() {
        RequestState previous = CURRENT.get();
        CURRENT.set(new RequestState());
        return new RequestScope(previous);
    }

    /** The handle returned by {@link #beginRequest()}; closing it restores the enclosing scope. */
    public static final class RequestScope implements AutoCloseable {
        private final RequestState previous;

        private RequestScope(RequestState previous) {
            this.previous = previous;
        }

        @Override
        public void close() {
            // remove() rather than set(null): a pooled thread that keeps a ThreadLocal entry alive holds
            // its value (and its classloader) until that thread dies.
            if (previous == null) {
                CURRENT.remove();
            } else {
                CURRENT.set(previous);
            }
        }
    }
    private static Map<String, String> user;
    private static final Map<String, String> TAGS = new LinkedHashMap<>();
    private static final Map<String, Map<String, Object>> CONTEXTS = new LinkedHashMap<>();
    private static final Deque<Map<String, Object>> BREADCRUMBS = new ArrayDeque<>();

    private ConduxScope() {
    }

    /** Attach the signed-in user (id/email/username) to subsequent events; null clears it. */
    public static void setUser(Map<String, String> next) {
        Map<String, String> value = next == null ? null : new LinkedHashMap<>(next);
        RequestState request = CURRENT.get();
        if (request != null) {
            request.user = value;
            return;
        }
        synchronized (LOCK) {
            user = value;
        }
    }

    /** Attach a tag to subsequent events; a null value removes it. */
    public static void setTag(String key, String value) {
        RequestState request = CURRENT.get();
        if (request != null) {
            if (value == null) {
                request.tags.remove(key);
            } else {
                request.tags.put(key, value);
            }
            return;
        }
        synchronized (LOCK) {
            if (value == null) {
                TAGS.remove(key);
            } else {
                TAGS.put(key, value);
            }
        }
    }

    /** Attach a named context object to subsequent events; null removes it. */
    public static void setContext(String name, Map<String, Object> context) {
        RequestState request = CURRENT.get();
        if (request != null) {
            if (context == null) {
                request.contexts.remove(name);
            } else {
                request.contexts.put(name, new LinkedHashMap<>(context));
            }
            return;
        }
        synchronized (LOCK) {
            if (context == null) {
                CONTEXTS.remove(name);
            } else {
                CONTEXTS.put(name, new LinkedHashMap<>(context));
            }
        }
    }

    /** Record a breadcrumb; the trail (newest last, capped) rides every subsequent event. */
    public static void addBreadcrumb(Breadcrumb crumb) {
        Map<String, Object> wire = crumb.toWire();
        RequestState request = CURRENT.get();
        if (request != null) {
            request.breadcrumbs.addLast(wire);
            while (request.breadcrumbs.size() > MAX_BREADCRUMBS) {
                request.breadcrumbs.removeFirst();
            }
            return;
        }
        synchronized (LOCK) {
            BREADCRUMBS.addLast(wire);
            while (BREADCRUMBS.size() > MAX_BREADCRUMBS) {
                BREADCRUMBS.removeFirst();
            }
        }
    }

    /** Reset all ambient state (tests, or a full sign-out). Clears the request scope when one is active. */
    public static void clear() {
        RequestState request = CURRENT.get();
        if (request != null) {
            request.user = null;
            request.tags.clear();
            request.contexts.clear();
            request.breadcrumbs.clear();
            return;
        }
        synchronized (LOCK) {
            user = null;
            TAGS.clear();
            CONTEXTS.clear();
            BREADCRUMBS.clear();
        }
    }

    /**
     * The scope's contribution to an event, holding only the keys that are actually set so an unenriched
     * event keeps its exact wire shape. Breadcrumbs use the Sentry {@code {"values": []}} envelope.
     */
    static Map<String, Object> fields() {
        RequestState request = CURRENT.get();

        Map<String, String> mergedUser;
        Map<String, String> mergedTags;
        Map<String, Map<String, Object>> mergedContexts;
        List<Map<String, Object>> mergedTrail;
        synchronized (LOCK) {
            mergedUser = user == null ? null : new LinkedHashMap<>(user);
            mergedTags = new LinkedHashMap<>(TAGS);
            mergedContexts = new LinkedHashMap<>(CONTEXTS);
            mergedTrail = new ArrayList<>(BREADCRUMBS);
        }

        // The request scope layers OVER the process state rather than replacing it, so a request keeps
        // the deployment-wide tags while overriding the ones it sets itself.
        if (request != null) {
            if (request.user != null) {
                mergedUser = new LinkedHashMap<>(request.user);
            }
            mergedTags.putAll(request.tags);
            mergedContexts.putAll(request.contexts);
            // Concatenated, not merged: the trail is a sequence, and the process-level crumbs genuinely
            // happened before the ones recorded during the request.
            mergedTrail.addAll(request.breadcrumbs);
            while (mergedTrail.size() > MAX_BREADCRUMBS) {
                mergedTrail.remove(0);
            }
        }

        Map<String, Object> fields = new LinkedHashMap<>();
        if (mergedUser != null) {
            fields.put("user", mergedUser);
        }
        if (!mergedTags.isEmpty()) {
            fields.put("tags", mergedTags);
        }
        if (!mergedContexts.isEmpty()) {
            fields.put("contexts", mergedContexts);
        }
        if (!mergedTrail.isEmpty()) {
            fields.put("breadcrumbs", Map.of("values", mergedTrail));
        }
        return fields;
    }

    /** One step of the trail: a navigation, a job, a request — whatever helps replay the path. */
    public static final class Breadcrumb {
        private final String message;
        private String category;
        private Level level;
        private String type;
        private Map<String, Object> data;
        private Double timestamp;

        private Breadcrumb(String message) {
            this.message = message;
        }

        public static Breadcrumb of(String message) {
            return new Breadcrumb(message);
        }

        public Breadcrumb category(String category) {
            this.category = category;
            return this;
        }

        public Breadcrumb level(Level level) {
            this.level = level;
            return this;
        }

        public Breadcrumb type(String type) {
            this.type = type;
            return this;
        }

        public Breadcrumb data(Map<String, Object> data) {
            this.data = data;
            return this;
        }

        /** Epoch seconds; stamped from the clock when left unset. */
        public Breadcrumb timestamp(double timestamp) {
            this.timestamp = timestamp;
            return this;
        }

        Map<String, Object> toWire() {
            Map<String, Object> wire = new LinkedHashMap<>();
            wire.put("message", message);
            wire.put("timestamp", timestamp != null ? timestamp : System.currentTimeMillis() / 1000.0);
            if (category != null) {
                wire.put("category", category);
            }
            if (level != null) {
                wire.put("level", level.wire());
            }
            if (type != null) {
                wire.put("type", type);
            }
            if (data != null) {
                wire.put("data", data);
            }
            return wire;
        }
    }
}
