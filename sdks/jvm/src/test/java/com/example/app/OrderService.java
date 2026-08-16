package com.example.app;

/**
 * Stands in for customer code, and the package is the whole point.
 *
 * <p>Every other class in this test tree lives in {@code ai.condux}, which the SDK now correctly treats
 * as its own plumbing rather than the application. A test that threw from there and asserted the frame
 * was in-app would only have been passing because the test happens to share our namespace, which is an
 * accident and not a property of the code under test.
 */
public final class OrderService {

    private OrderService() {
    }

    /** Fails the way an application fails: its own type, from its own package. */
    public static void checkout() {
        throw new IllegalArgumentException("boom from java");
    }
}
