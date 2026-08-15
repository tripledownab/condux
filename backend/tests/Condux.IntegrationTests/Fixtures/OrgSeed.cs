using Condux.Core.Plans;
using Npgsql;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Test helper: put an org on a paid tier.
///
/// <para>POST /api/orgs deliberately creates every org on <see cref="Tier.Free"/> and ignores any tier
/// the caller sends — otherwise any signed-up user could self-assign Enterprise and get unlimited
/// ingest plus uncapped Conductor runs on the platform's model key. In production the only writer of
/// <c>orgs.tier</c> is the signature-verified Stripe webhook (ADR-0026), which keys on
/// <c>stripe_customer_id</c> and so cannot be used from a test that never went through checkout.</para>
///
/// <para>So tests set the tier out of band, exactly as the webhook does, and this helper is the only
/// place that does it. It lives in the test project on purpose: adding a general
/// <c>SetTier(orgId, tier)</c> to <c>OrgRepository</c> would put back the very writer the endpoint fix
/// removes, one layer down and available to any caller.</para>
/// </summary>
public static class OrgSeed
{
    /// <summary>Sets an existing org's tier directly, the way the Stripe webhook would.</summary>
    public static async Task SetTierAsync(
        string connectionString, long orgId, Tier tier, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE orgs SET tier = @t WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("t", (short)(int)tier);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Overload for the tests that carry a tier as a plain int.</summary>
    public static Task SetTierAsync(
        string connectionString, long orgId, int tier, CancellationToken ct = default) =>
        SetTierAsync(connectionString, orgId, (Tier)tier, ct);
}
