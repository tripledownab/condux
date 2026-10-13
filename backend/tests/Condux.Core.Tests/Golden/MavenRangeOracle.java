/**
 * Used by maven-ranges.py. Reads "range\tversion" lines and prints, for each Maven artifact jar named
 * on the command line, 1 when every comparator of the range admits the version under that jar's
 * ComparableVersion, 0 when one does not, and x when the range is not a list of comparators.
 */
import java.io.BufferedReader;
import java.io.File;
import java.io.InputStreamReader;
import java.io.PrintWriter;
import java.lang.reflect.Constructor;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;

public class MavenRangeOracle {
    public static void main(String[] jars) throws Exception {
        Constructor<?>[] versions = new Constructor<?>[jars.length];
        for (int i = 0; i < jars.length; i++) {
            ClassLoader loader = new URLClassLoader(new URL[] {new File(jars[i]).toURI().toURL()}, null);
            versions[i] = loader.loadClass("org.apache.maven.artifact.versioning.ComparableVersion")
                    .getConstructor(String.class);
        }
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        PrintWriter out = new PrintWriter(System.out, false, StandardCharsets.UTF_8);
        for (String line; (line = in.readLine()) != null; ) {
            String[] fields = line.split("\t", -1);
            StringBuilder answers = new StringBuilder();
            for (Constructor<?> version : versions) {
                answers.append(answers.length() > 0 ? "\t" : "").append(decide(version, fields[0], fields[1]));
            }
            out.println(answers);
        }
        out.flush();
    }

    @SuppressWarnings({"unchecked", "rawtypes"})
    private static String decide(Constructor<?> version, String range, String running) throws Exception {
        Comparable target = (Comparable) version.newInstance(running);
        boolean admitted = true;
        for (String part : range.split(",")) {
            part = part.trim();
            String op = null;
            for (String candidate : new String[] {">=", "<=", ">", "<", "="}) {
                if (part.startsWith(candidate)) {
                    op = candidate;
                    break;
                }
            }
            String bound = op == null ? "" : part.substring(op.length()).trim();
            if (bound.isEmpty()) {
                return "x";
            }
            int order = Integer.signum(target.compareTo(version.newInstance(bound)));
            admitted &= switch (op) {
                case ">=" -> order >= 0;
                case "<=" -> order <= 0;
                case ">" -> order > 0;
                case "<" -> order < 0;
                default -> order == 0;
            };
        }
        return admitted ? "1" : "0";
    }
}
