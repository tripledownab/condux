namespace Condux.Core.FixEngine;

/// <summary>The scrubbed, scoped context handed to a provider for one fix run. The provider commits the
/// change on a new branch and opens a draft PR into <see cref="BaseBranch"/> (the project's linked base).</summary>
public sealed record FixRequest(string IssueId, string RepoFullName, string BaseBranch, string Prompt, string Model)
{
    /// <summary>The repo-relative files the fix should focus on (never the whole repo).</summary>
    public IReadOnlyList<string> ScopedPaths { get; init; } = [];

    /// <summary>The org's GitHub App installation for minting a repo-scoped token (0 = none).</summary>
    public long InstallationId { get; init; }

    /// <summary>The org the run belongs to, so a provider can resolve its BYO-key config (#65). 0 skips
    /// the lookup and uses the platform default.</summary>
    public long OrgId { get; init; }
}

/// <summary>What a provider returns after producing a draft PR: the head branch it pushed, the draft
/// PR URL, and a summary.</summary>
public sealed record FixResult(string Branch, string PrUrl, string Summary)
{
    /// <summary>Model tokens the run consumed (0 for the no-model backends) — audited per run so pricing
    /// can be calibrated against real cost (#100).</summary>
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }
}

/// <summary>
/// The execution backend for a fix run — "the Conductor". Two implementations
/// are planned: Anthropic Managed Agents (default, Claude) and a self-hosted
/// agent runtime for any bring-your-own model. Both must preserve the same
/// safety guarantees: sandboxed edits, credential isolation, and draft-PR-only.
/// </summary>
public interface IFixProvider
{
    /// <summary>Identifies the backend, e.g. "anthropic-managed-agents".</summary>
    string Name { get; }

    /// <summary>Runs the agentic loop and opens a draft PR, returning its URL.</summary>
    Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default);
}
