namespace Condux.Core.Http;

/// <summary>
/// The shape check for a URL an ADMIN supplies and this platform then fetches.
///
/// Two such settings exist and they are not the same policy: an enterprise SSO endpoint must be present,
/// while a model provider's base URL is optional because a provider with a fixed endpoint ignores it.
/// The difference is the caller's to state; what they share is this predicate, which is here so the two
/// cannot drift apart the way two hand-written copies of a rule already have elsewhere in this codebase.
///
/// <b>This checks the shape, not the destination.</b> It does not stop an admin naming an internal host,
/// and it cannot: a name resolves at connect time, so an address check here loses to DNS rebinding.
/// Keeping outbound requests off internal networks is an egress control in the deployment, which is what
/// ADR-0032 already committed to for the SSO token endpoint. What it does buy is that the value is a URL
/// with a scheme we speak, so it cannot be a <c>file://</c> read or a reference that resolves against
/// our own host.
/// </summary>
public static class HttpUrls
{
    /// <summary>True when the value is an absolute URL whose scheme is http or https.</summary>
    public static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}
