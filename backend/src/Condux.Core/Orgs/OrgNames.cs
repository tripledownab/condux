namespace Condux.Core.Orgs;

/// <summary>
/// An organization's name, and the display slug derived from it. The server owns both: a slug used to be
/// derived in the browser and posted alongside the name, which made a value the database constrained
/// depend on code the database could not see.
/// </summary>
public static class OrgNames
{
    /// <summary>Longest accepted name. Generous, since an org names itself and we display it verbatim.</summary>
    public const int MaxLength = 120;

    /// <summary>The slug when a name yields no sluggable characters, so a slug is never empty.</summary>
    private const string Fallback = "org";

    /// <summary>
    /// A name is anything non-blank within the length cap. Deliberately permissive about script and
    /// punctuation: an organization named in Japanese or Arabic is a customer, not a bad request.
    /// </summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxLength;

    /// <summary>
    /// Lower-cases the name and joins its ASCII alphanumeric runs with single dashes. Display only: it
    /// appears in the admin console and the impersonation banner, and nothing looks an org up by it, so
    /// it carries no uniqueness requirement and callers must not give it one.
    ///
    /// A name with no ASCII alphanumerics at all becomes <c>org</c> rather than the empty string. The
    /// empty string is what the browser used to produce for such a name, and it was accepted, which is
    /// how a column that reads as descriptive came to hold a blank.
    /// </summary>
    public static string ToSlug(string name)
    {
        var slug = new System.Text.StringBuilder(name.Length);
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var trimmed = slug.ToString().TrimEnd('-');
        return trimmed.Length == 0 ? Fallback : trimmed;
    }
}
