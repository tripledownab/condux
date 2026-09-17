using System.Text;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent;

/// <summary>
/// What the Conductor says to a model: the two system prompts, and the user message carrying the
/// assembled context plus the scoped files. Separate from the gateway because this is what we say, and
/// the gateway is how we run it; a second provider reuses these rather than writing its own.
/// </summary>
internal static class AgentPrompts
{
    // Truncation cap, reflected in the label when hit so a truncated file is never silently short.
    internal const int MaxCharsPerFile = 48_000;

    internal const string SystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. Respond with ONLY a "
        + "JSON object, no code fences and no prose: {\"summary\": \"what the fix does and why\", "
        + "\"files\": [{\"path\": \"repo/relative/path\", \"contents\": \"the COMPLETE new file contents\"}]}. "
        + "Address the root cause, change only what the fix requires, and keep the existing code style. "
        + "Add or update a test that fails without your change and passes with it. If you conclude the "
        + "code is already correct and no such test can be written, say so in the summary and change "
        + "nothing. Every entry in files must contain the full file, not a diff. "
        + UntrustedText.Guidance;

    internal const string AgentSystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. Use the tools to read "
        + "the code before changing it, then write complete file contents (never a diff). Address the root "
        + "cause, change only what the fix requires, and keep the existing code style. Add or update a test "
        + "that fails without your change and passes with it. If you conclude the code is already correct "
        + "and no such test can be written, say so in the summary and change nothing. Call finish with a "
        + "short summary once the fix is complete. " + UntrustedText.Guidance;

    internal static string BuildUserMessage(AgentRunSpec spec, IReadOnlyList<RepoFile> files)
    {
        // No empty-file case: a run that read nothing fails before reaching the model.
        var sb = new StringBuilder(spec.Prompt);
        sb.AppendLine().AppendLine();
        // Interpolated into our sentence, so neutralizing is the whole defence. Neither is our text:
        // RepoEndpoints stores a repository name and a default branch with no shape check.
        sb.AppendLine(
            $"Repository: {UntrustedText.Neutralize(spec.RepoFullName)} "
            + $"(base branch: {UntrustedText.Neutralize(spec.BaseBranch)})");

        // Repository contents are not ours either, and the previous <file> tag was forgeable: nothing
        // stripped "</file>" from the body, so a file containing that string closed its own block and
        // everything after it read as top level. The path had the same hole, since RepoPaths rejects
        // absolute paths, drive letters and traversal but permits a quote in a filename.
        foreach (var file in files)
        {
            var truncated = file.Content.Length > MaxCharsPerFile;
            var label = $"contents of {file.Path}" + (truncated ? " (truncated)" : "");
            // Fence, not AppendFenced: every file here exists, so an EMPTY one must still appear. Omitted
            // instead, the model cannot tell an empty file from one that was never fetched.
            sb.AppendLine(UntrustedText.Fence(
                label, truncated ? file.Content[..MaxCharsPerFile] : file.Content));
        }

        return sb.ToString();
    }
}
