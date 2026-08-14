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
    private static Map<String, String> user;
    private static final Map<String, String> TAGS = new LinkedHashMap<>();
    private static final Map<String, Map<String, Object>> CONTEXTS = new LinkedHashMap<>();
    private static final Deque<Map<String, Object>> BREADCRUMBS = new ArrayDeque<>();

    private ConduxScope() {
    }

    /** Attach the signed-in user (id/email/username) to subsequent events; null clears it. */
    public static void setUser(Map<String, String> next) {
        synchronized (LOCK) {
            user = next == null ? null : new LinkedHashMap<>(next);
        }
    }

    /** Attach a tag to subsequent events; a null value removes it. */
    public static void setTag(String key, String value) {
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
        synchronized (LOCK) {
            BREADCRUMBS.addLast(wire);
            while (BREADCRUMBS.size() > MAX_BREADCRUMBS) {
                BREADCRUMBS.removeFirst();
            }
        }
    }

    /** Reset all ambient state (tests, or a full sign-out). */
    public static void clear() {
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
        Map<String, Object> fields = new LinkedHashMap<>();
        synchronized (LOCK) {
            if (user != null) {
                fields.put("user", new LinkedHashMap<>(user));
            }
            if (!TAGS.isEmpty()) {
                fields.put("tags", new LinkedHashMap<>(TAGS));
            }
            if (!CONTEXTS.isEmpty()) {
                fields.put("contexts", new LinkedHashMap<>(CONTEXTS));
            }
            if (!BREADCRUMBS.isEmpty()) {
                List<Map<String, Object>> values = new ArrayList<>(BREADCRUMBS);
                fields.put("breadcrumbs", Map.of("values", values));
            }
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
