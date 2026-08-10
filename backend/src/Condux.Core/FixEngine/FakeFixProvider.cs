namespace Condux.Core.FixEngine;

/// <summary>
/// A deterministic <see cref="IFixProvider"/> that fabricates a draft-PR result without calling any
/// LLM or GitHub. It backs tests and CI (which must never touch a real model or repo) and lets the
/// whole Conductor flow run end to end before the Managed Agents provider lands. The PR URL points at
/// a reserved non-routable domain so it can never be mistaken for a real pull request.
/// </summary>
public sealed class FakeFixProvider : IFixProvider
{
    public string Name => "fake";

    public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
    {
        var branch = $"condux/fix-{request.IssueId}";
        var prUrl = $"https://example.invalid/{request.RepoFullName}/pull/0";
        var summary =
            $"Draft fix for issue {request.IssueId}: {branch} → {request.BaseBranch} (fake provider — no real changes).";
        return Task.FromResult(new FixResult(branch, prUrl, summary));
    }
}
