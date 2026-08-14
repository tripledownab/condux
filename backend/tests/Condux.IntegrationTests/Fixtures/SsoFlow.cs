using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>Shared helpers for the enterprise-SSO flow tests (OIDC + SAML): browser-redirect parsing,
/// the provisioning assertion, and a self-signed test-IdP certificate.</summary>
internal static class SsoFlow
{
    /// <summary>A fresh self-signed certificate playing an IdP's signing key.</summary>
    public static X509Certificate2 MakeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=idp.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public static string QueryParam(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }
        return "";
    }

    /// <summary>How many members with this email the org has — the "did SSO provision?" probe.</summary>
    public static async Task<int> CountMembershipAsync(string connectionString, long orgId, string email)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM org_members m JOIN users u ON u.id = m.user_id WHERE m.org_id = @org AND u.email = @email",
            conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("email", email);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
