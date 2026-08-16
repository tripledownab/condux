package ai.condux;

import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Which frames the SDK calls the application's own.
 *
 * <p>This exists because of a measurement, not a hunch. A real Spring Boot application (petclinic, run
 * through the proving ground) produced 53 frames of which 48 were marked in-app, and exactly ONE of
 * those was the application: the rest were Tomcat, Spring Web, {@code jakarta.servlet}, and a frame of
 * this SDK's own servlet filter. The relay puts every in-app frame into the grouping fingerprint, so a
 * Spring upgrade that shifts those frames splits one issue in two, and the fix engine follows in-app
 * frames back to a repository, so it was being pointed at framework source.
 *
 * <p>Stacks are synthetic, set with {@link Throwable#setStackTrace}. That is deliberate: it pins the
 * classifier itself, deterministically, without needing Spring or a servlet container on the test
 * classpath, and without depending on which package this test happens to live in.
 */
public final class InAppFramesTest {
    private static final String DSN = "https://testkey@ingest.test/proj-uuid";
    private static int checks;
    private static int failures;

    public static void main(String[] args) {
        runtimeAndFrameworkFramesAreNotTheApplication();
        thisSdksOwnFramesAreNotTheApplication();
        inAppPackagesReplacesTheHeuristic();

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    /** The default heuristic, against the stack shape a real Spring request actually produces. */
    private static void runtimeAndFrameworkFramesAreNotTheApplication() {
        List<Boolean> flags = capture(wire(), springLikeStack("com.acme.checkout.OrderController"));

        // Oldest-first on the wire, so this reads bottom of the stack upwards: Thread, Tomcat,
        // jakarta.servlet, this SDK's filter, Spring Web, then the application's controller.
        check(flags.equals(List.of(false, false, false, false, false, true)),
            "only the application frame is in-app; the runtime, the container, the servlet API, this SDK "
                + "and Spring Web are not");
    }

    /**
     * The one with a privacy dimension as well as a correctness one, and the reason the Python SDK grew
     * the same exclusion: our own frames are monitoring plumbing, never the user's code.
     */
    private static void thisSdksOwnFramesAreNotTheApplication() {
        IllegalStateException error = new IllegalStateException("boom");
        error.setStackTrace(new StackTraceElement[] {
            new StackTraceElement("ai.condux.servlet.ConduxExceptionFilter", "doFilter", "ConduxExceptionFilter.java", 60),
            new StackTraceElement("ai.condux.ConduxClient", "captureException", "ConduxClient.java", 79),
        });

        check(capture(wire(), error).equals(List.of(false, false)), "no frame of this SDK is ever in-app");
    }

    /** An explicit allowlist replaces the guess outright, which is what an unusually-packaged app needs. */
    private static void inAppPackagesReplacesTheHeuristic() {
        // petclinic genuinely lives under org.springframework, so the default heuristic cannot tell it
        // apart from the framework and marks nothing in-app. That is the honest cost of a prefix
        // heuristic with no path signal to work from, and the reason the override has to exist. Asserted
        // rather than glossed over, so the limitation is on the record.
        IllegalStateException frameworkNamespaced =
            springLikeStack("org.springframework.samples.petclinic.OwnerController");
        check(capture(wire(), frameworkNamespaced).equals(List.of(false, false, false, false, false, false)),
            "an application inside a framework's namespace has no in-app frames by default");

        check(capture(wire("org.springframework.samples."), frameworkNamespaced)
                .equals(List.of(false, false, false, false, false, true)),
            "a listed package is in-app even though the default heuristic would exclude it");

        // And the inverse, which is what makes it a replacement rather than an addition: listing
        // something puts everything else out, including a frame the default would have called the
        // application's.
        check(capture(wire("com.acme."), springLikeStack("com.other.Thing"))
                .equals(List.of(false, false, false, false, false, false)),
            "an unlisted application package is not in-app once inAppPackages is set");
    }

    /** The frame shape of a Spring MVC request failure, newest frame first, as the JVM reports it. */
    private static IllegalStateException springLikeStack(String applicationClass) {
        IllegalStateException error = new IllegalStateException("checkout failed");
        error.setStackTrace(new StackTraceElement[] {
            new StackTraceElement(applicationClass, "boom", "OwnerController.java", 42),
            new StackTraceElement("org.springframework.web.servlet.DispatcherServlet", "doDispatch", "DispatcherServlet.java", 1089),
            new StackTraceElement("ai.condux.servlet.ConduxExceptionFilter", "doFilter", "ConduxExceptionFilter.java", 60),
            new StackTraceElement("jakarta.servlet.http.HttpServlet", "service", "HttpServlet.java", 590),
            new StackTraceElement("org.apache.catalina.core.StandardWrapperValve", "invoke", "StandardWrapperValve.java", 197),
            new StackTraceElement("java.lang.Thread", "run", "Thread.java", 840),
        });
        return error;
    }

    /** A client and the recorder wired into it, so a test can read back exactly what that client sent. */
    private record Wired(ConduxClient client, Recorder recorder) {
    }

    private static Wired wire(String... inAppPackages) {
        Recorder recorder = new Recorder(List.of(Scripted.ok(200)));
        ConduxClient.Builder builder = ConduxClient.builder(DSN)
            .transport(recorder.transport())
            .sleeper(recorder.sleeper())
            .clock(() -> 1_700_000_000.5);
        if (inAppPackages.length > 0) {
            builder = builder.inAppPackages(inAppPackages);
        }
        return new Wired(builder.build(), recorder);
    }

    /** Capture the error and read back the in_app flag of every frame, in wire order. */
    private static List<Boolean> capture(Wired wired, Throwable error) {
        wired.client().captureException(error);
        Matcher matcher = Pattern.compile("\"in_app\":(true|false)").matcher(wired.recorder().bodies.get(0));
        List<Boolean> flags = new ArrayList<>();
        while (matcher.find()) {
            flags.add(Boolean.parseBoolean(matcher.group(1)));
        }
        return flags;
    }

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) {
            failures++;
            System.err.println("FAIL: " + message);
        }
    }

}
