using Condux.Core.FixEngine;

namespace Condux.Core.Tests;

/// <summary>
/// Helpers for asserting on an assembled prompt.
/// </summary>
/// <remarks>
/// <see cref="UntrustedText.Guidance"/> quotes both delimiters in order to explain them, so any
/// assertion that counts markers or checks one is absent has to exclude it first. Two tests got that
/// wrong before this existed, each passing or failing for a reason unrelated to what it was testing.
/// </remarks>
internal static class PromptText
{
    /// <summary>The prompt with the guidance sentence removed, so only real regions remain.</summary>
    public static string WithoutGuidance(string prompt) =>
        prompt.Replace(UntrustedText.Guidance, string.Empty, StringComparison.Ordinal);

    /// <summary>How many times <paramref name="needle"/> appears in <paramref name="haystack"/>.</summary>
    public static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
