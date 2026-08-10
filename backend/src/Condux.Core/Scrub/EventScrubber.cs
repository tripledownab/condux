using Condux.Core.Events;

namespace Condux.Core.Scrub;

/// <summary>
/// Applies <see cref="Scrubber"/> across a full <see cref="Event"/> — message,
/// exception values, breadcrumb messages, and tag/extra maps — so secrets never
/// reach storage or a model. This is the second scrub layer (the relay also
/// scrubs the raw body on ingest).
/// </summary>
public static class EventScrubber
{
    public static Event Scrub(Event e) => e with
    {
        Message = e.Message is null ? null : Scrubber.ScrubString(e.Message),
        Exceptions = e.Exceptions.Select(ScrubException).ToList(),
        Breadcrumbs = e.Breadcrumbs
            .Select(b => b with
            {
                Message = b.Message is null ? null : Scrubber.ScrubString(b.Message),
                Data = ScrubMap(b.Data),
            })
            .ToList(),
        Tags = ScrubMap(e.Tags),
        Extra = ScrubMap(e.Extra),
        Modules = ScrubMap(e.Modules),
        // Privacy-first user capture (#105): derive the pseudonymous counting key from the raw
        // identifiers, then redact the email (ScrubString redacts email shapes) and drop the IP —
        // neither ever reaches storage.
        UserKey = e.UserKey.Length > 0 ? e.UserKey : UserKeys.Derive(e.User),
        User = e.User is null ? null : e.User with
        {
            Id = e.User.Id is null ? null : Scrubber.ScrubString(e.User.Id),
            Username = e.User.Username is null ? null : Scrubber.ScrubString(e.User.Username),
            Email = e.User.Email is null ? null : Scrubber.ScrubString(e.User.Email),
            IpAddress = null,
        },
        Request = e.Request is null ? null : e.Request with
        {
            Url = e.Request.Url is null ? null : Scrubber.ScrubString(e.Request.Url),
            QueryString = e.Request.QueryString is null ? null : Scrubber.ScrubString(e.Request.QueryString),
            Headers = ScrubMap(e.Request.Headers),
        },
        Contexts = ScrubMap(e.Contexts),
    };

    private static ExceptionValue ScrubException(ExceptionValue x) =>
        x with
        {
            Value = x.Value is null ? null : Scrubber.ScrubString(x.Value),
            Stacktrace = x.Stacktrace is null
                ? null
                : x.Stacktrace with { Frames = x.Stacktrace.Frames.Select(ScrubFrame).ToList() },
        };

    // Source context and locals are code content and can carry secrets; every line and value goes
    // through the string scrub, and sensitive var names are redacted outright.
    private static Frame ScrubFrame(Frame f) => f with
    {
        ContextLine = f.ContextLine is null ? null : Scrubber.ScrubString(f.ContextLine),
        ContextBefore = f.ContextBefore.Select(Scrubber.ScrubString).ToList(),
        ContextAfter = f.ContextAfter.Select(Scrubber.ScrubString).ToList(),
        Vars = ScrubMap(f.Vars),
    };

    private static IReadOnlyDictionary<string, string> ScrubMap(IReadOnlyDictionary<string, string> map)
    {
        var result = new Dictionary<string, string>(map.Count);
        foreach (var (key, value) in map)
        {
            result[key] = Scrubber.IsSensitiveKey(key) ? "[redacted]" : Scrubber.ScrubString(value);
        }
        return result;
    }
}
