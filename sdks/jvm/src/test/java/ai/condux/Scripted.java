package ai.condux;

import java.util.Map;

/** A scripted response: an HTTP status (+ headers) or a network error to throw. */
record Scripted(Integer status, Map<String, String> headers, String error) {
    static Scripted ok(int status) {
        return new Scripted(status, Map.of(), null);
    }

    static Scripted status(int status, Map<String, String> headers) {
        return new Scripted(status, headers, null);
    }

    static Scripted error(String message) {
        return new Scripted(null, Map.of(), message);
    }
}
