namespace Condux.ControlPlane.Contracts;

/// <summary>
/// A validated offset/limit page window. The bounds live here as the single source, so no endpoint carries
/// an inline <c>?? 25</c> or re-checks <c>1..100</c>. Each caller hoists and passes its own default page
/// size; an out-of-range request resolves to <c>null</c>, which the caller turns into a 400. <see cref="Require"/>
/// is for surfaces whose client always sends both values (a missing one is an error, not a default).
/// </summary>
internal readonly record struct PageParams(int Limit, int Offset)
{
    public const int MaxLimit = 100;
    private const int MinLimit = 1;
    private const int FirstOffset = 0;

    /// <summary>Optional paging: an omitted limit falls to the caller's <paramref name="defaultLimit"/>, an
    /// omitted offset to the first page.</summary>
    public static PageParams? Resolve(int? limit, int? offset, int defaultLimit) =>
        Bounded(limit ?? defaultLimit, offset ?? FirstOffset);

    /// <summary>Required paging: both values must be present and in range.</summary>
    public static PageParams? Require(int? limit, int? offset) =>
        limit is { } take && offset is { } skip ? Bounded(take, skip) : null;

    private static PageParams? Bounded(int limit, int offset) =>
        limit >= MinLimit && limit <= MaxLimit && offset >= FirstOffset ? new PageParams(limit, offset) : null;
}
