namespace Condux.Core.FixEngine;

/// <summary>A finished command: what it returned, and what it printed.</summary>
public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// What an agentic run is allowed to execute, and how much of the output may travel back into the
/// conversation.
///
/// <para>
/// <b>This is not the security boundary. The container is.</b> Most of what the list permits runs code the
/// repository controls: <c>make</c> executes a Makefile, <c>npm run</c> and its siblings execute package
/// scripts, <c>cargo</c> builds run <c>build.rs</c>, and Gradle and Maven run plugins. Any allow list that
/// lets a fix run its own test suite necessarily lets the repository decide what executes. Treat this as
/// keeping the agent on task and refusing the obvious reach for a network or a credential tool, and rely on
/// isolation, resource caps and a dropped capability set for the part that actually contains a hostile
/// command.
/// </para>
///
/// <para>
/// Widening the list is still a deliberate edit in one place, and the parsing below still refuses the
/// shapes that would smuggle a second command past a first-word check. Both are worth having. Neither is
/// what stops arbitrary execution.
/// </para>
/// </summary>
public static class CommandPolicy
{
    /// <summary>
    /// Executables an agent may invoke, matched exactly against the command's first word. Build and test
    /// runners, plus the vulnerability scanners. Several of these run repository-defined code by design;
    /// see the note on the class about what that means.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "dotnet", "npm", "pnpm", "yarn", "node", "tsc",
        "python", "python3", "pytest", "go", "cargo",
        "mvn", "gradle", "bundle", "rake", "rspec",
        "composer", "php", "make",
        "osv-scanner", "trivy",
    };

    /// <summary>
    /// Characters that would chain, redirect or substitute a second command. A workspace runs the parsed
    /// argument vector directly and never through a shell, so these are already inert; rejecting them as
    /// well means a workspace that regresses to a shell does not silently become an escape hatch.
    /// </summary>
    private static readonly char[] Shell = [';', '&', '|', '`', '$', '>', '<', '\n', '\r'];

    private const int MaxCommandLength = 512;

    /// <summary>How much command output may re-enter the conversation, in characters.</summary>
    public const int MaxOutputChars = 8_000;

    /// <summary>
    /// Parse and authorize a command. Returns the argument vector to execute, or an explanation to hand
    /// back to the model. A refusal is a normal tool result, not a fault: the agent asked for something it
    /// may not have and can try another way.
    /// </summary>
    public static bool TryAuthorize(string command, out IReadOnlyList<string> argv, out string error)
    {
        argv = [];
        var trimmed = (command ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            error = "A command is required.";
            return false;
        }

        if (trimmed.Length > MaxCommandLength)
        {
            error = $"Command is longer than the {MaxCommandLength} character limit.";
            return false;
        }

        if (trimmed.IndexOfAny(Shell) >= 0)
        {
            error = "Commands run directly, not through a shell, so pipes, redirects, "
                + "substitutions and chained commands are not available. Run one command at a time.";
            return false;
        }

        var parsed = Split(trimmed);
        if (parsed.Count == 0)
        {
            error = "A command is required.";
            return false;
        }

        if (!Allowed.Contains(parsed[0]))
        {
            error = $"'{parsed[0]}' is not an allowed command. Allowed: {string.Join(", ", Allowed.Order())}.";
            return false;
        }

        argv = parsed;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Bound command output to what is worth spending tokens on, keeping both ends. A failing test suite
    /// prints its summary last and its first failure first, so dropping the middle preserves the two parts
    /// that explain the failure while a naive head-truncation would keep neither.
    /// </summary>
    public static string BoundOutput(string output, int maxChars = MaxOutputChars)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= maxChars)
        {
            return output ?? string.Empty;
        }

        var half = maxChars / 2;
        var dropped = output.Length - (half * 2);
        return string.Concat(
            output.AsSpan(0, half),
            $"\n\n... {dropped} characters omitted ...\n\n",
            output.AsSpan(output.Length - half));
    }

    /// <summary>
    /// Split on whitespace, honouring double quotes so a single argument may contain spaces (a test filter
    /// is the common case). Quotes are removed, since the argument vector is passed to the process
    /// directly and nothing downstream would strip them.
    /// </summary>
    private static List<string> Split(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var c in command)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
