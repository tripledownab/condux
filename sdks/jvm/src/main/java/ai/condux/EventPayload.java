package ai.condux;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Builds the Sentry "exception" payload (type / value / mechanism / stacktrace) from a Throwable. */
final class EventPayload {
    /**
     * Frames that are never the application's, when it has not said which packages are.
     *
     * <p>Every other Condux SDK can answer this from the file path, because a dependency lands somewhere
     * recognisable: {@code site-packages}, {@code /gems/}, {@code /vendor/}, {@code node_modules}. The JVM
     * has no such signal, since a StackTraceElement carries a class name and a bare file name and every
     * class arrives from a jar, so this has to work on package prefixes.
     *
     * <p>Three groups, and the third is where the judgement is:
     *
     * <ol>
     *   <li>The language and its runtime. {@code jakarta.} belongs here for exactly the reason
     *       {@code javax.} does: it IS {@code javax.}, renamed by Jakarta EE, and omitting it left every
     *       servlet frame marked as application code.
     *   <li>This SDK. Monitoring plumbing is not the user's code, and marking it in-app puts our own
     *       frames into their grouping and their culprit. The Python SDK had the same defect, fixed
     *       there for the same reason.
     *   <li>The layers that wrap EVERY request in an instrumented web application. These appear in every
     *       stack, so they do not distinguish one error from another, but they DO enter the fingerprint,
     *       which means a framework upgrade that shifts them splits one issue into two.
     * </ol>
     *
     * <p>Group three is bounded deliberately: request-handling infrastructure, not dependencies in
     * general, because on the JVM that list has no end and a half-finished one is worse than an honest
     * heuristic. An application that needs anything else calls {@code inAppPackages}, which replaces this
     * with an allowlist.
     */
    private static final String[] NOT_APPLICATION_PREFIXES = {
        "java.", "javax.", "jakarta.", "jdk.", "sun.", "com.sun.", "kotlin.", "scala.", "groovy.",
        "ai.condux.",
        "org.springframework.", "org.apache.", "org.eclipse.jetty.", "io.undertow.",
    };

    private EventPayload() {
    }

    static Map<String, Object> exception(Throwable error, boolean handled, List<String> inAppPackages) {
        Map<String, Object> exception = new LinkedHashMap<>();
        exception.put("type", error.getClass().getName());
        exception.put("value", error.getMessage() == null ? "" : error.getMessage());
        // A direct captureException is a handled capture (Sentry's default for user-invoked captures);
        // a framework adapter reports handled=false. The relay reads mechanism.handled for the unhandled badge.
        Map<String, Object> mechanism = new LinkedHashMap<>();
        mechanism.put("type", "generic");
        mechanism.put("handled", handled);
        exception.put("mechanism", mechanism);
        List<Object> frames = stackFrames(error, inAppPackages);
        if (!frames.isEmpty()) {
            Map<String, Object> stacktrace = new LinkedHashMap<>();
            stacktrace.put("frames", frames);
            exception.put("stacktrace", stacktrace);
        }
        return exception;
    }

    /**
     * A JVM stack trace is newest-first (the throw site first); reverse to oldest-first (throw site last),
     * the order the relay's fingerprinter and issue detail expect.
     */
    private static List<Object> stackFrames(Throwable error, List<String> inAppPackages) {
        StackTraceElement[] trace = error.getStackTrace();
        List<Object> frames = new ArrayList<>(trace.length);
        for (int i = trace.length - 1; i >= 0; i--) {
            StackTraceElement element = trace[i];
            Map<String, Object> frame = new LinkedHashMap<>();
            frame.put("function", element.getMethodName());
            frame.put("module", element.getClassName());
            if (element.getFileName() != null) {
                frame.put("filename", element.getFileName());
            }
            if (element.getLineNumber() > 0) {
                frame.put("lineno", element.getLineNumber());
            }
            frame.put("in_app", inApp(element.getClassName(), inAppPackages));
            frames.add(frame);
        }
        return frames;
    }

    /**
     * Whether a frame is the application's own. Drives grouping, the culprit, and which files the fix
     * engine follows back to a repository, so a frame wrongly marked in-app is not cosmetic.
     *
     * <p>An explicit {@code inAppPackages} replaces the heuristic outright rather than adding to it. A
     * caller that has said which packages are theirs has answered the question completely, and treating
     * their list as a hint on top of ours would leave them unable to express "only these".
     */
    private static boolean inApp(String className, List<String> inAppPackages) {
        if (!inAppPackages.isEmpty()) {
            for (String prefix : inAppPackages) {
                if (className.startsWith(prefix)) {
                    return true;
                }
            }
            return false;
        }
        for (String prefix : NOT_APPLICATION_PREFIXES) {
            if (className.startsWith(prefix)) {
                return false;
            }
        }
        return true;
    }
}
