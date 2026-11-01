using Condux.ControlPlane.Contracts;
using Npgsql;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Answers a request whose text Postgres cannot store with <c>400 text_not_storable</c>, rather than the
/// 500 and self-report an escaping exception becomes.
/// </summary>
/// <remarks>
/// U+0000 is valid text and valid JSON, and Postgres <c>text</c> refuses it with 22021 (a <c>jsonb</c>
/// parameter never carries one: <c>PostgresJson.AddJsonb</c> replaces it), so a note, a name or
/// an issue query carrying one failed in whichever repository it reached. The caller sent it, and one place
/// answers it for every route, the MCP endpoint included, instead of a check in front of each statement.
/// Events are a different case: the relay keeps them and replaces the character (<c>EventJson</c>). A
/// response already started cannot be changed, so that exception still escapes as before.
/// </remarks>
internal static class UnstorableText
{
    public const string Error = "text_not_storable";

    public static IApplicationBuilder UseUnstorableTextRefusal(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.CharacterNotInRepertoire && !context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(Error));
            }
        });
}
