package ai.condux;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.DoubleConsumer;

/**
 * Replays a scripted sequence of responses (the last repeats); records requests, bodies, and backoff
 * delays. Shared by every suite in this package so the wire assertions all read the same recorded JSON.
 */
final class Recorder {
    final List<Map<String, String>> requests = new ArrayList<>();
    final List<String> urls = new ArrayList<>();
    final List<String> bodies = new ArrayList<>();
    final List<Double> delays = new ArrayList<>();
    private final List<Scripted> responses;

    Recorder(List<Scripted> responses) {
        this.responses = responses;
    }

    Transport transport() {
        return (url, headers, body) -> {
            urls.add(url);
            requests.add(headers);
            bodies.add(body);
            Scripted response = responses.get(Math.min(bodies.size() - 1, responses.size() - 1));
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
