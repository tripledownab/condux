using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>The result of joining a user into an org under the single-org invariant (ADR-0018).</summary>
internal enum JoinOutcome
{
    /// <summary>Added to the org (any prior solo personal org was removed on the way in).</summary>
    Joined,

    /// <summary>Already a member of the target org — nothing changed.</summary>
    AlreadyMember,

    /// <summary>Belongs to a different shared org, so joining is refused.</summary>
    BlockedSharedOrg,
}

/// <summary>
/// The one place the single-org-per-user invariant (ADR-0018) is applied when adding a user to an org: a
/// solo personal org (the user is its only member) is deleted on the way in, membership in a different
/// shared org is refused, and re-joining the same org is a no-op. Shared by invite-accept (#84) and
/// enterprise-SSO domain provisioning (#72) so the rule can't drift between them; each caller maps the
/// returned <see cref="JoinOutcome"/> to its own surface (a 409 for the JSON API, a redirect for the SSO flow).
/// </summary>
internal static class MembershipProvisioning
{
    public static async Task<JoinOutcome> JoinSingleOrgAsync(
        OrgMemberRepository members, OrgRepository orgs, long userId, long orgId, OrgRole role,
        CancellationToken ct = default)
    {
        foreach (var membership in await members.ListOrgsForUserAsync(userId, ct))
        {
            if (membership.Org.Id == orgId)
            {
                return JoinOutcome.AlreadyMember;
            }
            if ((await members.ListByOrgAsync(membership.Org.Id, ct)).Count > 1)
            {
                return JoinOutcome.BlockedSharedOrg;
            }
            await orgs.DeleteAsync(membership.Org.Id, ct);
        }

        await members.AddAsync(orgId, userId, role, ct);
        return JoinOutcome.Joined;
    }
}
