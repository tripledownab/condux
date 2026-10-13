namespace Condux.Core.FixEngine;

/// <summary>
/// The name of the branch a fix run pushes its draft pull request from. It is derived from the run's own
/// id, which the run row keeps, so the stale-run sweep can rebuild it and ask the source host whether a run
/// it is about to fail had in fact opened its pull request before it stopped. The whole id is used: run ids
/// are UUIDv7, whose leading characters are a timestamp, so a prefix would repeat across runs a minute apart.
/// </summary>
public static class FixBranches
{
    /// <summary>The branch for a run. <paramref name="reference"/> is what the run fixes: the issue id, or
    /// the advisory id for a dependency bump.</summary>
    public static string ToBranch(string reference, Guid runId) => $"condux/fix-{reference}-{runId:N}";
}
