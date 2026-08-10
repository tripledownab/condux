package ai.condux;

/** Event severity, matching the levels the relay understands. The wire value is the lowercase string. */
public enum Level {
    DEBUG("debug"),
    INFO("info"),
    WARNING("warning"),
    ERROR("error"),
    FATAL("fatal");

    private final String wire;

    Level(String wire) {
        this.wire = wire;
    }

    /** The lowercase string sent on the wire. */
    public String wire() {
        return wire;
    }
}
