package ai.condux;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.function.DoubleConsumer;

/**
 * Replays a scripted sequence of responses (the last repeats); records requests, bodies, and backoff
 * delays. Shared by every suite in this package so the wire assertions all read the same recorded JSON.
 *
 * <p>The lists are synchronized because the request-scope suite captures from several threads at once to
 * reproduce a concurrency bug. A plain ArrayList would race there, and an error monitor's own test
 * harness losing writes is the last place anyone would look.
 */
final class Recorder {
    final List<Map<String, String>> requests = Collections.synchronizedList(new ArrayList<>());
    final List<String> urls = Collections.synchronizedList(new ArrayList<>());
    final List<String> bodies = Collections.synchronizedList(new ArrayList<>());
    final List<Double> delays = Collections.synchronizedList(new ArrayList<>());
    private final List<Scripted> responses;

    Recorder(List<Scripted> responses) {
        this.responses = responses;
    }

    Transport transport() {
        return (url, headers, body) -> {
            int index;
            // One atomic append, so two concurrent captures cannot interleave into mismatched rows or
            // both read the same size and pick the same scripted response.
            synchronized (this) {
                urls.add(url);
                requests.add(headers);
                bodies.add(body);
                index = bodies.size() - 1;
            }
            Scripted response = responses.get(Math.min(index, responses.size() - 1));
            if (response.error() != null) {
                throw new RuntimeException(response.error());
            }
            return new Transport.Response(response.status(), response.headers());
        };
    }

    DoubleConsumer sleeper() {
        return delays::add;
    }
}
