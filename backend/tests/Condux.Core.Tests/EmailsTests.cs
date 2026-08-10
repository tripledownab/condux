using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class EmailsTests
{
    [Fact]
    public void Normalize_trims_and_lowercases()
    {
        Assert.Equal("ada@example.com", Emails.Normalize("  Ada@Example.COM "));
    }

    [Theory]
    [InlineData("ada@example.com")]
    [InlineData("a.b+tag@sub.example.co.uk")]
    public void IsValid_accepts_reasonable_addresses(string email) => Assert.True(Emails.IsValid(email));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@no-local")]
    [InlineData("no-domain@")]
    [InlineData("two@at@signs.com")]
    [InlineData("has space@example.com")]
    public void IsValid_rejects_malformed_addresses(string email) => Assert.False(Emails.IsValid(email));
}
