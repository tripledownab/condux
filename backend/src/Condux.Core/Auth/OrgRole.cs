namespace Condux.Core.Auth;

/// <summary>
/// A user's role within an org. Values are ordered so a simple <c>role &gt;= required</c> comparison
/// gates access (owner outranks admin outranks member). Persisted as the numeric value.
/// </summary>
public enum OrgRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

/// <summary>Wire (de)serialization for <see cref="OrgRole"/> — stable lower-case names for the API.</summary>
public static class OrgRoles
{
    /// <summary>The lower-case wire name used in API payloads.</summary>
    public static string Name(OrgRole role) => role switch
    {
        OrgRole.Owner => "owner",
        OrgRole.Admin => "admin",
        _ => "member",
    };

    /// <summary>Parses a wire role name (case-insensitive); false if it isn't a known role.</summary>
    public static bool TryParse(string? value, out OrgRole role)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "owner": role = OrgRole.Owner; return true;
            case "admin": role = OrgRole.Admin; return true;
            case "member": role = OrgRole.Member; return true;
            default: role = OrgRole.Member; return false;
        }
    }
}
