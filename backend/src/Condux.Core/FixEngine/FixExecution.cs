namespace Condux.Core.FixEngine;

/// <summary>
/// Where an org's fix runs execute (ADR-0033 slice 4c, #68). Persisted in <c>orgs.fix_execution</c>.
///
/// This is the only thing that differs between the two deployments. The context assembly, the double
/// scrub, the allowance reservation, the cost cap and the audit trail are the same either way, and both
/// end in a draft pull request a human reviews.
/// </summary>
public enum FixExecution
{
    /// <summary>Condux runs the fix (the default, and what every org did before runners existed).</summary>
    Hosted = 0,

    /// <summary>The org's own runner leases the work and runs it on their compute with their
    /// credentials.</summary>
    Runner = 1,
}
