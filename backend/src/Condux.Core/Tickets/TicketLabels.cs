namespace Condux.Core.Tickets;

/// <summary>A label a tracker offers for a team, live: its id, name and the group it belongs to, if any.</summary>
public sealed record TicketLabelOption(string Id, string Name, string? GroupId);

/// <summary>The labels a save keeps, with their live names, or the closed reason it is refused.</summary>
public sealed record TicketLabelChoice(IReadOnlyList<TicketLabelOption> Kept, string? Error)
{
    public static TicketLabelChoice Refuse(string error) => new([], error);
}

/// <summary>
/// Decides which labels a project's save keeps (ADR-0053). One rule set, so the save and its tests agree on
/// what a label the tracker no longer offers means.
/// </summary>
public static class TicketLabels
{
    /// <summary>
    /// A label the caller adds must be on the tracker's live list. A label the project already had and the
    /// tracker no longer offers is dropped rather than refused: it was deleted or withdrawn on the tracker's
    /// side, and refusing it would refuse every later save, even unticking auto-create, with nothing to fix
    /// but guessing which label went. Two labels from one group are refused, since a group lets an issue
    /// carry one of its labels; Linear's schema says that of groups without a type, and refusing every pair
    /// is the cautious reading until a typed group has been tried.
    /// </summary>
    /// <param name="stored">The project's labels as saved. One the tracker no longer offers is dropped only
    /// when <paramref name="offeredIsComplete"/>; from a list cut short it is kept as saved, since a label past
    /// the end of a page ceiling has not gone anywhere.</param>
    public static TicketLabelChoice Resolve(
        IReadOnlyList<string?> wanted, IReadOnlyList<TicketLabelOption> stored, IReadOnlyList<TicketLabelOption> offered,
        bool offeredIsComplete = true)
    {
        if (wanted.Any(string.IsNullOrWhiteSpace))
        {
            return TicketLabelChoice.Refuse("linear_label_invalid");
        }
        // A label can arrive on two pages of a paged list, so the first copy of an id wins.
        var live = offered.DistinctBy(l => l.Id).ToDictionary(l => l.Id);
        var kept = new List<TicketLabelOption>();
        foreach (var id in wanted.Distinct().Select(id => id!))
        {
            if (live.TryGetValue(id, out var label))
            {
                kept.Add(label);
            }
            else if (stored.FirstOrDefault(l => l.Id == id) is not { } saved)
            {
                return TicketLabelChoice.Refuse("linear_label_unknown");
            }
            else if (!offeredIsComplete)
            {
                kept.Add(saved);
            }
        }
        return kept.Where(l => l.GroupId is not null).GroupBy(l => l.GroupId).Any(g => g.Count() > 1)
            ? TicketLabelChoice.Refuse("linear_labels_same_group")
            : new TicketLabelChoice(kept, null);
    }
}
