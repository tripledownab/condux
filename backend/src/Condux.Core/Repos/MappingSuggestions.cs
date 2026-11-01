namespace Condux.Core.Repos;

/// <summary>The answer to "suggest mappings from errors": the rules to offer, and which of a closed set of
/// reasons applies, so an empty list can say why it is empty instead of one message for every case.</summary>
public sealed record MappingSuggestions(IReadOnlyList<DerivedMapping> Suggestions, string Outcome)
{
    /// <summary>At least one rule to offer.</summary>
    public const string Suggested = "suggested";

    /// <summary>No GitHub App, or the org has no installation, so the repo tree cannot be read.</summary>
    public const string NotConnected = "not_connected";

    /// <summary>The project's recent events carry no in-app frame to match.</summary>
    public const string NoFrames = "no_frames";

    /// <summary>Nothing could be offered, and not because of the ties <see cref="Ambiguous"/> names: usually
    /// some frame matched no file in the repo, or only by its file name, which points to a different repo or
    /// branch. Other frames may still have matched.</summary>
    public const string NoMatch = "no_match";

    /// <summary>Every frame matched a file in the repo, some only by tying between two or more by more than
    /// the file name (sibling apps with the same layout), and no rule could be offered without guessing
    /// between them, so nothing says which directory is theirs. A mapping has to be added by hand. A frame
    /// that matched nothing, or only by its file name, makes the answer <see cref="NoMatch"/> instead, since
    /// that is what a frame from another repo or branch looks like. Named only when no rule was derived:
    /// beside one, the answer follows that rule.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>Every rule the frames support is already a mapping, so there is nothing to add.</summary>
    public const string AlreadyMapped = "already_mapped";

    /// <summary>A rule the frames support has its stack root already mapped: in this repository to a
    /// different source root, or in another repository the project links to any source root at all, since
    /// a frame two repositories claim links into neither. It is not offered, since a second mapping for one
    /// stack root ties with the first and which of the two applies would depend on list order; the existing
    /// one has to go first. Named only when nothing else is offered: beside a fresh rule the answer is
    /// <see cref="Suggested"/>.</summary>
    public const string Conflicting = "conflicting";

    /// <summary>GitHub or the event store did not answer. Why is in the server log, not here.</summary>
    public const string Unavailable = "unavailable";

    public static MappingSuggestions None(string outcome) => new([], outcome);
}
