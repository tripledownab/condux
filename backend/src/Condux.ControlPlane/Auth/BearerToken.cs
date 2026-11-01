namespace Condux.ControlPlane.Auth;

/// <summary>
/// Extracts a raw bearer token from the <c>Authorization</c> header (the CI machine-credential path, e.g.
/// scoped release tokens). Returns null when the header is missing or is not a non-empty <c>Bearer</c>
/// value. Shared by the token-authed endpoints so the header parse lives in one place.
///
/// Reading a bearer token is also what marks a route as a bearer route, so its 401 challenges with
/// <c>Bearer</c> (<see cref="AuthChallenge"/>) without the route having to say so.
/// </summary>
internal static class BearerToken
{
    public static string? From(HttpContext http)
    {
        AuthChallenge.UseScheme(http, AuthChallenge.Bearer);
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var raw = header["Bearer ".Length..].Trim();
        return raw.Length > 0 ? raw : null;
    }
}
