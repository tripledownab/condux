package ai.condux;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.function.DoubleConsumer;
import java.util.function.DoubleSupplier;

/**
 * Condux SDK for the JVM — report errors to a Condux relay from Java, Kotlin, or Scala.
 *
 * <p>Emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[]) so the relay
 * normalizes it exactly like an official Sentry SDK: swap the DSN and it works. Delivery is resilient
 * (429 / 5xx / network failures retry with backoff, honoring Retry-After) and never throws — a failed
 * send returns a {@link SendResult} you can inspect. The transport, sleep, and clock are injectable via
 * the builder so backoff is exercised with no real network or timers. Zero dependencies: HTTP is
 * {@link java.net.http.HttpClient} ({@link EventTransport}) and the JSON is written by hand ({@link Json}).
 *
 * <pre>{@code
 * ConduxClient condux = ConduxClient.builder("https://<key>@ingest.condux.ai/<projectId>")
 *     .environment("production")
 *     .release("1.4.2")
 *     .build();
 * try {
 *     doWork();
 * } catch (Exception e) {
 *     condux.captureException(e);
 *     throw e;
 * }
 * }</pre>
 */
public final class ConduxClient {
    private final EventTransport transport;
    private final String environment;
    private final String release;
    private final DoubleSupplier clock;

    private ConduxClient(Builder builder) {
        Dsn dsn = Dsn.parse(builder.dsn);
        Map<String, String> headers = Map.of("Content-Type", "application/json", "x-condux-auth", dsn.publicKey());
        Transport http = builder.transport != null ? builder.transport : EventTransport.httpClient();
        DoubleConsumer sleeper = builder.sleeper != null ? builder.sleeper : EventTransport::defaultSleep;
        this.transport = new EventTransport(dsn.storeUrl(), headers, builder.maxRetries, http, sleeper);
        this.environment = builder.environment;
        this.release = builder.release;
        this.clock = builder.clock != null ? builder.clock : () -> System.currentTimeMillis() / 1000.0;
    }

    /** Start building a client for the given project DSN. */
    public static Builder builder(String dsn) {
        return new Builder(dsn);
    }

    /** Report an exception as an error-level event, with its stack trace. Never throws on delivery failure. */
    public SendResult captureException(Throwable error) {
        return captureException(error, true);
    }

    /**
     * Report an exception, marking whether it was {@code handled}. A framework adapter that catches an
     * uncaught request exception reports {@code handled = false} (drives the unhandled badge).
     */
    public SendResult captureException(Throwable error, boolean handled) {
        Map<String, Object> fields = new LinkedHashMap<>();
        fields.put("level", Level.ERROR.wire());
        fields.put("exception", Map.of("values", List.of(EventPayload.exception(error, handled))));
        return dispatch(fields);
    }

    /** Report a bare message event at the given level. */
    public SendResult captureMessage(String message, Level level) {
        Map<String, Object> fields = new LinkedHashMap<>();
        fields.put("level", level.wire());
        fields.put("message", message);
        return dispatch(fields);
    }

    private SendResult dispatch(Map<String, Object> fields) {
        Map<String, Object> event = new LinkedHashMap<>();
        event.put("event_id", UUID.randomUUID().toString().replace("-", "")); // 32 lowercase hex
        event.put("timestamp", clock.getAsDouble());                          // epoch seconds
        event.put("platform", "java");
        event.putAll(ConduxScope.fields());
        event.putAll(fields);
        if (environment != null) {
            event.put("environment", environment);
        }
        if (release != null) {
            event.put("release", release);
        }
        return transport.send(Json.write(event));
    }

    /** Fluent builder; the DSN is required, everything else is optional (and testing hooks). */
    public static final class Builder {
        private final String dsn;
        private String environment;
        private String release;
        private int maxRetries = 3;
        private Transport transport;
        private DoubleConsumer sleeper;
        private DoubleSupplier clock;

        private Builder(String dsn) {
            this.dsn = dsn;
        }

        public Builder environment(String environment) {
            this.environment = environment;
            return this;
        }

        public Builder release(String release) {
            this.release = release;
            return this;
        }

        public Builder maxRetries(int maxRetries) {
            this.maxRetries = maxRetries;
            return this;
        }

        /** Override the HTTP transport (tests inject a scripted one). */
        public Builder transport(Transport transport) {
            this.transport = transport;
            return this;
        }

        /** Override the backoff sleep (tests record the delays instead of waiting). */
        public Builder sleeper(DoubleConsumer sleeper) {
            this.sleeper = sleeper;
            return this;
        }

        /** Override the event clock (tests pin the timestamp). */
        public Builder clock(DoubleSupplier clock) {
            this.clock = clock;
            return this;
        }

        public ConduxClient build() {
            return new ConduxClient(this);
        }
    }
}
