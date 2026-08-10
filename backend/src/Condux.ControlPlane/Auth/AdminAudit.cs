using System.Text.Json;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Writes a platform-admin audit entry (ADR-0027) from an endpoint, stamping the admin's REAL identity as
/// the actor (even mid-impersonation, since <c>HttpContext.User</c> stays the admin). Keeps the endpoints
/// declarative: they pass an action + optional target ids + a details object, which is serialized to the
/// jsonb column.
/// </summary>
internal static class AdminAudit
{
    public static Task WriteAsync(
        HttpContext http, AdminAuditRepository audit, string action,
        long? targetOrgId = null, long? targetUserId = null, object? details = null) =>
        audit.WriteAsync(
            OrgAuthorization.CurrentUserId(http.User), OrgAuthorization.CurrentUserEmail(http.User),
            action, targetOrgId, targetUserId,
            details is null ? "{}" : JsonSerializer.Serialize(details), http.RequestAborted);
}
