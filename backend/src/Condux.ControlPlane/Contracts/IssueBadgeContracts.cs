namespace Condux.ControlPlane.Contracts;

/// <summary>The per-user count of issues new or regressed since the user last opened a project's issue
/// list — the "new issues" nav badge (ADR-0030).</summary>
public sealed record NewIssueCountResponse(int Count);
