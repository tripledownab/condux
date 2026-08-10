namespace Condux.Core.Repos;

/// <summary>A GitHub repo linked to a project — the anchor for error→code linking and fixes.</summary>
public sealed record RepoLink(Guid Id, long ProjectId, string RepoFullName, string DefaultBranch, DateTimeOffset CreatedAt);

/// <summary>A prefix rule mapping a runtime stack-frame path to a path inside the repo.</summary>
public sealed record CodeMapping(Guid Id, Guid RepoLinkId, string StackRoot, string SourceRoot);

/// <summary>A release tied to the commit it deployed, so a fix runs against the right ref.</summary>
public sealed record Release(
    Guid Id, long ProjectId, Guid RepoLinkId, string Version, string CommitSha, DateTimeOffset CreatedAt);
