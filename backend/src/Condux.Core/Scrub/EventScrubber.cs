using Condux.Core.Events;

namespace Condux.Core.Scrub;

/// <summary>
/// Applies <see cref="Scrubber"/> across a full <see cref="Event"/>: message, exception values,
/// breadcrumb messages, frame context and locals, and the breadcrumb data, tag, extra, context, request
/// header and module maps, so secrets never reach storage or a model.
///
/// <para><b>This is the only scrub on the ingest path, not one of two.</b> A previous version of this
/// comment said the relay also scrubbed the raw request body first. It does not, and it has no code
/// that ever did: both ingest endpoints parse, call this once, and publish the result. The correction
/// matters because a reader deciding how much this pass can safely be relaxed was being told there was
/// another one behind it.</para>
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
        // Modules keys are package names by convention, not field names, so the sensitive key rule does
        // not fit here: it substring matches "token", "secret", "password" and "apikey", which redacted
        // the version of jsonwebtoken, csrf-token, secretbox and tokenizer. Those are ordinary
        // dependencies, and their installed version is what a CVE exposure read needs (ADR-0041).
        // Values still go through the string scrub. Nothing validates these keys, so the exemption does
        // give up a protection, and it is accepted because that protection was incidental rather than
        // designed: it fired only when a key happened to contain one of four substrings, and no shape
        // check replaces it, since a short unprefixed secret is not distinguishable from a version
        // string. Reviewed deliberately, 2026-09-03. Fuller reasoning in ADR-0041.
        Modules = ScrubMap(e.Modules, redactSensitiveKeys: false),
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

    /// <param name="redactSensitiveKeys">
    /// Whether a key that names a credential drops its value outright. True for every map whose key is
    /// a field name chosen by the reporting app: a header, a tag, an extra, a frame local. False only
    /// where the key is data in its own right, which today means <see cref="Event.Modules"/> alone.
    /// </param>
    private static IReadOnlyDictionary<string, string> ScrubMap(
        IReadOnlyDictionary<string, string> map, bool redactSensitiveKeys = true)
    {
        var result = new Dictionary<string, string>(map.Count);
        foreach (var (key, value) in map)
        {
            result[key] = redactSensitiveKeys && Scrubber.IsSensitiveKey(key)
                ? "[redacted]"
                : Scrubber.ScrubString(value);
        }
        return result;
    }
}
