namespace Condux.ControlPlane.Auth;

/// <summary>
/// Puts a <c>WWW-Authenticate</c> challenge on every 401 the control-plane sends. RFC 9110 15.5.2: "The
/// server generating a 401 response MUST send a WWW-Authenticate header field". It is added where every
/// 401 passes, the response, rather than at each of the call sites that produce one, so a route added
/// later cannot leave it out. A route that writes its own challenge keeps it: this only fills an absence.
///
/// <para>The scheme names the credential that was refused. <c>Bearer</c> for a route that reads a bearer
/// token, which <see cref="BearerToken.From"/> marks on the way through, with <c>error="invalid_token"</c>
/// when a token was sent and refused (RFC 6750 3.1). The others are descriptive names rather than schemes a
/// client can answer, because the credentials they name never travel in <c>Authorization</c>: <c>Password</c>
/// where a password sent in a form was refused, a webhook's signature header, and <c>Session</c>, the session
/// cookie, for every other route. A <c>Bearer</c> challenge on those would claim a credential the route
/// refuses. Chromium and Firefox act only on Basic, Digest, NTLM and Negotiate, so none of these brings up a
/// login dialog over the dashboard.</para>
/// </summary>
internal static class AuthChallenge
{
    public const string Bearer = "Bearer";
    public const string Password = "Password";
    private const string Session = "Session";
    private const string Realm = "realm=\"condux\"";
    private static readonly object SchemeKey = new();

    /// <summary>Names the scheme this request's 401, if it gets one, should challenge with.</summary>
    public static void UseScheme(HttpContext http, string scheme) => http.Items[SchemeKey] = scheme;

    public static IApplicationBuilder UseAuthChallenge(this IApplicationBuilder app) =>
        app.Use((http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                if (http.Response.StatusCode == StatusCodes.Status401Unauthorized
                    && !http.Response.Headers.ContainsKey("WWW-Authenticate"))
                {
                    http.Response.Headers.WWWAuthenticate = ToChallenge(http);
                }
                return Task.CompletedTask;
            });
            return next(http);
        });

    private static string ToChallenge(HttpContext http)
    {
        var scheme = http.Items[SchemeKey] as string ?? Session;
        return scheme == Bearer && BearerToken.From(http) is not null
            ? $"{Bearer} {Realm}, error=\"invalid_token\""
            : $"{scheme} {Realm}";
    }
}
