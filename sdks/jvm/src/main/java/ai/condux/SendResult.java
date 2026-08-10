package ai.condux;

/**
 * Outcome of a delivery attempt sequence. Never thrown — inspect {@link #ok()}. {@code status} is null
 * when every attempt failed at the network level (no HTTP response); {@code error} carries the last
 * network failure message in that case.
 */
public record SendResult(boolean ok, int attempts, Integer status, String error) {
}
