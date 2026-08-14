namespace Condux.Sdk;

/// <summary>
/// Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
/// leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
/// it — the first triage questions ("which customer, which plan, what did they do last") answered without
/// threading anything through capture calls. The relay already scrubs all of these at ingest and derives
/// the pseudonymous users-affected key from the user fields.
/// </summary>
/// <remarks>
/// The scope is process wide, like every other Condux SDK, so it applies to whichever
/// <see cref="ConduxClient"/> reports and a framework integration shares it with application code. Every
/// member is safe to call from any thread.
/// </remarks>
public static class ConduxScope
{
    /// <summary>The trail length kept: newest wins, so a long-lived process drops the oldest crumbs
    /// rather than growing without bound.</summary>
    public const int MaxBreadcrumbs = 30;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Tags = [];
    private static readonly Dictionary<string, IReadOnlyDictionary<string, object?>> Contexts = [];
    private static readonly List<Breadcrumb> Trail = [];
    private static ConduxUser? currentUser;

    /// <summary>Attach the signed-in user to subsequent events; <c>null</c> clears it (a sign-out).</summary>
    public static void SetUser(ConduxUser? user)
    {
        lock (Gate)
        {
            currentUser = user;
        }
    }

    /// <summary>Attach a tag to subsequent events; a <c>null</c> value removes it.</summary>
    public static void SetTag(string key, string? value)
    {
        lock (Gate)
        {
            if (value is null)
            {
                Tags.Remove(key);
            }
            else
            {
                Tags[key] = value;
            }
        }
    }

    /// <summary>Attach a named context object to subsequent events; a <c>null</c> context removes it.</summary>
    public static void SetContext(string name, IReadOnlyDictionary<string, object?>? context)
    {
        lock (Gate)
        {
            if (context is null)
            {
                Contexts.Remove(name);
            }
            else
            {
                // Copied, so a later edit of the caller's dictionary cannot change events already sent.
                Contexts[name] = new Dictionary<string, object?>(context);
            }
        }
    }

    /// <summary>Record a breadcrumb; the trail (newest last, capped at <see cref="MaxBreadcrumbs"/>) rides
    /// every subsequent event. <paramref name="timestamp"/> is epoch seconds, stamped for you when omitted.</summary>
    public static void AddBreadcrumb(
        string message,
        string? category = null,
        Level? level = null,
        string? type = null,
        IReadOnlyDictionary<string, object?>? data = null,
        double? timestamp = null)
    {
        var crumb = new Breadcrumb
        {
            Message = message,
            Category = category,
            Level = level?.ToString().ToLowerInvariant(),
            Type = type,
            Data = data is null ? null : new Dictionary<string, object?>(data),
            Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };

        lock (Gate)
        {
            Trail.Add(crumb);
            if (Trail.Count > MaxBreadcrumbs)
            {
                Trail.RemoveRange(0, Trail.Count - MaxBreadcrumbs);
            }
        }
    }

    /// <summary>Reset all ambient state (tests, or a full sign-out).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            currentUser = null;
            Tags.Clear();
            Contexts.Clear();
            Trail.Clear();
        }
    }

    // The scope's contribution to an event, holding only the parts that are actually set: the payload
    // omits nulls, so an unenriched event keeps its exact wire shape. Snapshotted under the lock, because
    // another thread may enrich while this event serializes.
    internal static ScopeFields Fields()
    {
        lock (Gate)
        {
            return new ScopeFields(
                currentUser,
                Tags.Count > 0 ? new Dictionary<string, string>(Tags) : null,
                Contexts.Count > 0 ? new Dictionary<string, IReadOnlyDictionary<string, object?>>(Contexts) : null,
                Trail.Count > 0 ? new BreadcrumbEnvelope { Values = [.. Trail] } : null);
        }
    }
}

/// <summary>The signed-in user. The relay hashes the strongest identifier and redacts the raw email, so
/// these drive the users-affected count without storing the values.</summary>
public sealed record ConduxUser
{
    public string? Id { get; init; }

    public string? Email { get; init; }

    public string? Username { get; init; }
}

// The scope snapshot a single event is built from.
internal readonly record struct ScopeFields(
    ConduxUser? User,
    IReadOnlyDictionary<string, string>? Tags,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? Contexts,
    BreadcrumbEnvelope? Breadcrumbs);
