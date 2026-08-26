using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class RecoveryCodeTests
{
    [Fact]
    public void Generates_ten_distinct_codes_with_matching_hashes()
    {
        var (raw, hashes) = RecoveryCodes.Generate();

        Assert.Equal(RecoveryCodes.Count, raw.Length);
        Assert.Equal(RecoveryCodes.Count, hashes.Length);
        Assert.Equal(RecoveryCodes.Count, raw.Distinct().Count());
        Assert.Equal(RecoveryCodes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void Formats_as_two_groups_of_five_from_the_unambiguous_alphabet()
    {
        var (raw, _) = RecoveryCodes.Generate();

        foreach (var code in raw)
        {
            var parts = code.Split('-');
            Assert.Equal(2, parts.Length);
            Assert.All(parts, part => Assert.Equal(5, part.Length));
            // I, L, O and U are never generated: they are the characters a reader confuses.
            Assert.DoesNotContain(code, c => c is 'I' or 'L' or 'O' or 'U');
        }
    }

    [Fact]
    public void A_stored_hash_matches_the_code_as_the_user_types_it_back()
    {
        var (raw, hashes) = RecoveryCodes.Generate();
        var typed = $"  {raw[0].ToLowerInvariant().Replace("-", " ")}  ";

        Assert.Equal(hashes[0], SessionTokens.HashToken(RecoveryCodes.Normalize(typed)));
    }

    // The half their implementation left out. Excluding these characters from the alphabet stops us
    // GENERATING an ambiguous code; it does nothing for a user who reads their own handwritten 0 as an
    // O and types that, which is exactly the case a written-down code exists to serve.
    [Theory]
    [InlineData("O", "0")]
    [InlineData("I", "1")]
    [InlineData("L", "1")]
    [InlineData("U", "V")]
    public void Folds_the_characters_a_human_transcribes_wrongly(string typed, string intended)
    {
        Assert.Equal(RecoveryCodes.Normalize(intended), RecoveryCodes.Normalize(typed));
    }

    [Fact]
    public void Normalization_strips_only_formatting_and_keeps_length()
    {
        Assert.Equal("ABCDE12345", RecoveryCodes.Normalize(" abcde-12345 "));
        Assert.Equal("ABCDE12345", RecoveryCodes.Normalize("ABCDE 12345"));
    }
}
