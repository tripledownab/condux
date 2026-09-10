package ai.condux;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes. The cases live in that
 * file rather than here so the seven transports assert against one artifact instead of seven readings of
 * one sentence in a comment. See the file for why it is data and not prose.
 *
 * <p>Tab-separated rather than JSON precisely so this suite can read it: the JVM SDK is dependency-free
 * and ships a JSON writer but no parser, so a JSON fixture would be the one format the fleet could not
 * share. Splitting on a tab needs nothing.
 */
public final class BackoffConformanceTest {
    private static final String DSN = "https://testkey@ingest.test/proj-uuid";
    private static int checks;
    private static int failures;

    /** One fixture row: the sleep that must follow {@code attempt} for this response. */
    private record Case(int attempt, int status, String retryAfter, int expectedMs) {}

    public static void main(String[] args) throws IOException {
        List<Case> cases = readCases();
        // A fixture that failed to load reads exactly like one where every case passed, so the count is
        // asserted rather than assumed. A floor, so adding a case does not mean editing seven SDKs.
        check(cases.size() >= 15, "backoff.tsv loaded (" + cases.size() + " cases)");
        for (Case c : cases) {
            waitsExactlyAsLongAsTheFleetContractSays(c);
        }

        System.out.println(checks + " checks, " + failures + " failures");
        if (failures > 0) {
            System.exit(1);
        }
    }

    private static void waitsExactlyAsLongAsTheFleetContractSays(Case c) {
        Map<String, String> headers =
            c.retryAfter() == null ? Map.of() : Map.of("Retry-After", c.retryAfter());
        Recorder recorder = new Recorder(List.of(Scripted.status(c.status(), headers)));
        // One more retry than the attempt under test, so the sleep that follows it is recorded. The
        // scripted response repeats, so every attempt fails and the schedule runs to its end.
        ConduxClient client = ConduxClient.builder(DSN)
            .maxRetries(c.attempt() + 1)
            .transport(recorder.transport())
            .sleeper(recorder.sleeper())
            .build();

        client.captureMessage("hi", Level.INFO);

        String shown = c.retryAfter() == null ? "no Retry-After" : "Retry-After '" + c.retryAfter() + "'";
        String context = "attempt " + c.attempt() + ", status " + c.status() + ", " + shown;
        if (!check(recorder.delays.size() == c.attempt() + 1, context + ": one sleep per failed attempt")) {
            return; // the index below would throw rather than report, hiding every later case
        }
        long waited = Math.round(recorder.delays.get(c.attempt()) * 1000);
        check(waited == c.expectedMs(), context + ": waits " + c.expectedMs() + "ms, waited " + waited);
    }

    // The fixture is shared across the fleet, so it sits above this SDK. Walking up for it beats a fixed
    // relative hop, which would depend on which directory the suite happens to be launched from.
    private static List<Case> readCases() throws IOException {
        Path fixture = null;
        for (Path dir = Path.of("").toAbsolutePath(); dir != null; dir = dir.getParent()) {
            Path candidate = dir.resolve("sdks/conformance/backoff.tsv");
            if (Files.exists(candidate)) {
                fixture = candidate;
                break;
            }
        }
        if (fixture == null) {
            throw new IOException("sdks/conformance/backoff.tsv not found above " + Path.of("").toAbsolutePath());
        }

        List<Case> cases = new ArrayList<>();
        for (String line : Files.readAllLines(fixture)) {
            String text = line.trim();
            if (text.isEmpty() || text.startsWith("#")) {
                continue;
            }
            String[] columns = text.split("\t");
            String retryAfter = switch (columns[2]) {
                case "<none>" -> null;
                case "<empty>" -> "";
                default -> columns[2];
            };
            cases.add(new Case(
                Integer.parseInt(columns[0]), Integer.parseInt(columns[1]),
                retryAfter, Integer.parseInt(columns[3])));
        }
        return cases;
    }

    private static boolean check(boolean condition, String message) {
        checks++;
        if (!condition) {
            failures++;
            System.err.println("FAIL: " + message);
        }
        return condition;
    }
}
