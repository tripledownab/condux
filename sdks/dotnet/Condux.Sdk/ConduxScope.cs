namespace Condux.Sdk;

/// <summary>
/// Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
/// leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
/// it — the first triage questions ("which customer, which plan, what did they do last") answered without
/// threading anything through capture calls. The relay already scrubs all of these at ingest and derives
/// the pseudonymous users-affected key from the user fields.
/// </summary>
/// <remarks>
/// <para>
/// There are two layers, and the distinction is the whole point. Process state, set at startup and
/// shared by everything, is right for facts about the deployment. A request scope, opened by
/// <see cref="BeginRequest"/>, is right for facts about one request.
/// </para>
/// <para>
/// Without the second, <see cref="SetUser"/> in an ASP.NET Core controller is a cross-request leak: the
/// server handles requests concurrently, so one request's user would attach to another request's error.
/// That is worse than reporting no user, because it is confidently wrong and points an investigation at
/// the wrong customer. The middleware opens a scope per request, so a SetUser in a controller stays in
/// that request. Every member is safe to call from any thread.
/// </para>
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

    // AsyncLocal rather than ThreadStatic: an ASP.NET Core request hops threads across every await, and a
    // thread-static would be lost at the first one while also bleeding into whatever the thread pool runs
    // next. AsyncLocal follows the logical call context instead, so it stays with the request.
    //
    // Null means no request is in flight, so writes fall through to the process state above. That
    // fallback is what keeps a startup-time SetTag working exactly as it did before request scopes.
    private static readonly AsyncLocal<RequestState?> Current = new();

    // Each request gets its own instance, and only that request's logical flow can reach it, so it needs
    // no lock of its own. The process state below is shared and keeps Gate.
    private sealed class RequestState
    {
        public ConduxUser? User;
        public readonly Dictionary<string, string> Tags = [];
        public readonly Dictionary<string, IReadOnlyDictionary<string, object?>> Contexts = [];
        public readonly List<Breadcrumb> Trail = [];
    }

    /// <summary>
    /// Isolate enrichment to one request: anything set inside is visible only to events captured inside.
    /// Dispose to end it. The ASP.NET Core middleware wraps every request in this; call it directly
    /// around a background job, which has the same problem of many in flight at once.
    /// </summary>
    public static IDisposable BeginRequest()
    {
        var previous = Current.Value;
        Current.Value = new RequestState();
        return new RequestScope(previous);
    }

    private sealed class RequestScope(RequestState? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    /// <summary>Attach the signed-in user to subsequent events; <c>null</c> clears it (a sign-out).</summary>
    public static void SetUser(ConduxUser? user)
    {
        if (Current.Value is { } request)
        {
            request.User = user;
            return;
        }

        lock (Gate)
        {
            currentUser = user;
        }
    }

    /// <summary>Attach a tag to subsequent events; a <c>null</c> value removes it.</summary>
    public static void SetTag(string key, string? value)
    {
        if (Current.Value is { } request)
        {
            if (value is null)
            {
                request.Tags.Remove(key);
            }
            else
            {
                request.Tags[key] = value;
            }
            return;
        }

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
        if (Current.Value is { } request)
        {
            if (context is null)
            {
                request.Contexts.Remove(name);
            }
            else
            {
                request.Contexts[name] = new Dictionary<string, object?>(context);
            }
            return;
        }

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

        if (Current.Value is { } request)
        {
            request.Trail.Add(crumb);
            if (request.Trail.Count > MaxBreadcrumbs)
            {
                request.Trail.RemoveRange(0, request.Trail.Count - MaxBreadcrumbs);
            }
            return;
        }

        lock (Gate)
        {
            Trail.Add(crumb);
            if (Trail.Count > MaxBreadcrumbs)
            {
                Trail.RemoveRange(0, Trail.Count - MaxBreadcrumbs);
            }
        }
    }

    /// <summary>Reset all ambient state (tests, or a full sign-out). Clears the request scope when one
    /// is active, leaving the process state alone.</summary>
    public static void Clear()
    {
        if (Current.Value is { } request)
        {
            request.User = null;
            request.Tags.Clear();
            request.Contexts.Clear();
            request.Trail.Clear();
            return;
        }

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
    // The request scope layers OVER the process state rather than replacing it, so a request keeps the
    // deployment-wide tags while overriding the ones it sets itself.
    internal static ScopeFields Fields()
    {
        var request = Current.Value;

        ConduxUser? user;
        Dictionary<string, string> tags;
        Dictionary<string, IReadOnlyDictionary<string, object?>> contexts;
        List<Breadcrumb> trail;
        lock (Gate)
        {
            user = currentUser;
            tags = new Dictionary<string, string>(Tags);
            contexts = new Dictionary<string, IReadOnlyDictionary<string, object?>>(Contexts);
            trail = [.. Trail];
        }

        if (request is not null)
        {
            user = request.User ?? user;
            foreach (var (key, value) in request.Tags)
            {
                tags[key] = value;
            }
            foreach (var (name, value) in request.Contexts)
            {
                contexts[name] = value;
            }
            // Concatenated, not merged: the trail is a sequence, and the process-level crumbs genuinely
            // happened before the ones recorded during the request.
            trail.AddRange(request.Trail);
            if (trail.Count > MaxBreadcrumbs)
            {
                trail.RemoveRange(0, trail.Count - MaxBreadcrumbs);
            }
        }

        return new ScopeFields(
            user,
            tags.Count > 0 ? tags : null,
            contexts.Count > 0 ? contexts : null,
            trail.Count > 0 ? new BreadcrumbEnvelope { Values = [.. trail] } : null);
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
