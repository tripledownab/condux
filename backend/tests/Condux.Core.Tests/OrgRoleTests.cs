using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class OrgRoleTests
{
    [Fact]
    public void Roles_are_ordered_owner_above_admin_above_member()
    {
        Assert.True(OrgRole.Owner > OrgRole.Admin);
        Assert.True(OrgRole.Admin > OrgRole.Member);
    }

    [Theory]
    [InlineData(OrgRole.Owner, "owner")]
    [InlineData(OrgRole.Admin, "admin")]
    [InlineData(OrgRole.Member, "member")]
    public void Name_returns_the_lowercase_wire_value(OrgRole role, string expected) =>
        Assert.Equal(expected, OrgRoles.Name(role));

    [Theory]
    [InlineData("owner", OrgRole.Owner)]
    [InlineData("Admin", OrgRole.Admin)]
    [InlineData("  MEMBER ", OrgRole.Member)]
    public void TryParse_accepts_known_roles_case_insensitively(string input, OrgRole expected)
    {
        Assert.True(OrgRoles.TryParse(input, out var role));
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("superadmin")]
    public void TryParse_rejects_unknown_roles(string? input) =>
        Assert.False(OrgRoles.TryParse(input, out _));
}
