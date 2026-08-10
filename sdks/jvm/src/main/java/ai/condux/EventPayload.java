package ai.condux;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Builds the Sentry "exception" payload (type / value / mechanism / stacktrace) from a Throwable. */
final class EventPayload {
    private static final String[] SYSTEM_PREFIXES = {"java.", "javax.", "jdk.", "sun.", "com.sun.", "kotlin."};

    private EventPayload() {
    }

    static Map<String, Object> exception(Throwable error, boolean handled) {
        Map<String, Object> exception = new LinkedHashMap<>();
        exception.put("type", error.getClass().getName());
        exception.put("value", error.getMessage() == null ? "" : error.getMessage());
        // A direct captureException is a handled capture (Sentry's default for user-invoked captures);
        // a framework adapter reports handled=false. The relay reads mechanism.handled for the unhandled badge.
        Map<String, Object> mechanism = new LinkedHashMap<>();
        mechanism.put("type", "generic");
        mechanism.put("handled", handled);
        exception.put("mechanism", mechanism);
        List<Object> frames = stackFrames(error);
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
    private static List<Object> stackFrames(Throwable error) {
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
            frame.put("in_app", inApp(element.getClassName()));
            frames.add(frame);
        }
        return frames;
    }

    /** Application frames drive grouping + the culprit; the JDK and Kotlin runtime are noise. */
    private static boolean inApp(String className) {
        for (String prefix : SYSTEM_PREFIXES) {
            if (className.startsWith(prefix)) {
                return false;
            }
        }
        return true;
    }
}
