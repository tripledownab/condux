using Condux.Core.Plans;
using Condux.Core.Projects;

namespace Condux.Relay.Tests;

/// <summary>
/// Two projects of one org ("11" and "12", org 900) and one of another ("13", org 901), all on the Free
/// tier, plus one Enterprise project ("14", org 902), all under the key "devkey". Every org id differs from every project id, so a handler that meters on the project rather
/// than the org cannot pass a test by coincidence, as it can against the dev store, whose seeded project
/// is its own org with the same id.
/// </summary>
internal sealed class TwoOrgProjectStore : IProjectStore
{
    public Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default) =>
        Task.FromResult<Project?>(publicKey != "devkey" ? null : projectId switch
        {
            "11" => new Project("11", Tier.Free, "salt", OrgId: "900"),
            "12" => new Project("12", Tier.Free, "salt", OrgId: "900"),
            "13" => new Project("13", Tier.Free, "salt", OrgId: "901"),
            "14" => new Project("14", Tier.Enterprise, "salt", OrgId: "902"),
            _ => null,
        });
}
