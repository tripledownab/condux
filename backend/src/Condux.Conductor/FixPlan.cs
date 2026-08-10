using System.Text.Json;

namespace Condux.Conductor;

/// <summary>One file the fix rewrites: its repo-relative path and its complete new contents.</summary>
public sealed record FixPlanFile(string Path, string Contents);

/// <summary>The model's proposed fix: a human-readable summary and the full new contents of each changed
/// file. Whole-file contents (not diffs) keep the contract robust to apply — GitHub's contents API takes
/// the new blob directly.</summary>
public sealed record FixPlan(string Summary, IReadOnlyList<FixPlanFile> Files);

/// <summary>
/// Parses the model's output into a <see cref="FixPlan"/>. The prompt asks for a bare JSON object, but a
/// model may still wrap it in code fences or prose, so the parser extracts the outermost JSON object
/// before deserializing. Anything unusable throws — the orchestrator records the run FAILED.
/// </summary>
public static class FixPlanParser
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static FixPlan Parse(string modelOutput)
    {
        var start = modelOutput.IndexOf('{');
        var end = modelOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The model output contains no JSON object.");
        }

        WirePlan? wire;
        try
        {
            wire = JsonSerializer.Deserialize<WirePlan>(modelOutput[start..(end + 1)], Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The model output is not a valid fix plan: {ex.Message}");
        }

        var files = (wire?.Files ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Path) && f.Contents is not null)
            .Select(f => new FixPlanFile(f.Path!, f.Contents!))
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The fix plan has no usable file changes.");
        }

        return new FixPlan(wire?.Summary ?? "", files);
    }

    // The tolerant wire shape; validation above turns it into the non-null FixPlan.
    private sealed record WirePlan(string? Summary, List<WireFile>? Files);

    private sealed record WireFile(string? Path, string? Contents);
}
