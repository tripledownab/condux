using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Llm;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The BYO-key registry (#65): a per-org LLM provider config the Conductor uses instead of the platform
/// key. The API key is validated live, then stored encrypted (<see cref="SecretBox"/>) and never read
/// back. Opt-in behind <see cref="SecretsConfig"/> (404 when the secret store isn't configured) and
/// gated on the plan's <c>ByoKey</c> feature. Reads member+, writes admin+ (an org-level integration
/// secret, like the GitHub connect).
/// </summary>
internal static class LlmConfigEndpoints
{
    public static void MapLlmConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orgs/{orgId:long}/llm-config",
                async Task<Results<Ok<LlmConfigResponse>, NotFound>> (
                    long orgId, SecretsConfig secrets, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var store = http.RequestServices.GetRequiredService<PostgresLlmConfigStore>();
                    var config = await store.GetAsync(orgId, http.RequestAborted);
                    return config is null
                        ? TypedResults.NotFound()
                        : TypedResults.Ok(new LlmConfigResponse(
                            config.Provider, config.Model, config.BaseUrl, config.UpdatedAt));
                })
            .WithName("getLlmConfig").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapPut("/api/orgs/{orgId:long}/llm-config",
                async Task<Results<Ok<LlmConfigResponse>, NotFound, BadRequest<ErrorResponse>,
                    Conflict<ErrorResponse>>> (
                    long orgId, SetLlmConfigRequest request, SecretsConfig secrets, OrgRepository orgs,
                    HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    // BYO key is a plan feature (PlanCatalog.ByoKey) — Free/Team use the pooled key.
                    var org = await orgs.GetAsync(orgId);
                    if (org is null)
                    {
                        return TypedResults.NotFound();
                    }
                    if (!PlanCatalog.For((Tier)org.Tier).ByoKey)
                    {
                        return TypedResults.Conflict(new ErrorResponse("byo_key_requires_upgrade"));
                    }

                    if (!LlmProviders.IsSupported(request.Provider))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("provider_not_supported"));
                    }
                    var baseUrl = request.BaseUrl ?? "";
                    if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.ApiKey)
                        || (LlmProviders.RequiresBaseUrl(request.Provider) && string.IsNullOrWhiteSpace(baseUrl)))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_request"));
                    }

                    var validator = http.RequestServices.GetRequiredService<ILlmKeyValidator>();
                    if (await validator.ListModelsAsync(
                            request.Provider, baseUrl, request.ApiKey, http.RequestAborted) is null)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_key"));
                    }

                    var box = http.RequestServices.GetRequiredService<SecretBox>();
                    var store = http.RequestServices.GetRequiredService<PostgresLlmConfigStore>();
                    var stored = new StoredLlmConfig(
                        orgId, request.Provider, request.Model, baseUrl,
                        box.Seal(request.ApiKey), DateTimeOffset.UtcNow);
                    await store.UpsertAsync(stored, http.RequestAborted);
                    return TypedResults.Ok(new LlmConfigResponse(
                        stored.Provider, stored.Model, stored.BaseUrl, stored.UpdatedAt));
                })
            .WithName("setLlmConfig").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // List the models a key can use, to populate the settings picker. Uses the key in the body when
        // adding/replacing one, or the org's stored key when it is omitted (browsing the existing config).
        app.MapPost("/api/orgs/{orgId:long}/llm-config/models",
                async Task<Results<Ok<LlmModelsResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long orgId, ListLlmModelsRequest request, SecretsConfig secrets, HttpContext http) =>
                {
                    if (!secrets.Enabled || !LlmProviders.IsSupported(request.Provider))
                    {
                        return TypedResults.NotFound();
                    }

                    var apiKey = request.ApiKey;
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        // Fall back to the org's stored key (decrypted here, never returned).
                        var store = http.RequestServices.GetRequiredService<PostgresLlmConfigStore>();
                        var box = http.RequestServices.GetRequiredService<SecretBox>();
                        if (await store.GetAsync(orgId, http.RequestAborted) is not { } stored)
                        {
                            return TypedResults.BadRequest(new ErrorResponse("no_key"));
                        }
                        apiKey = box.Open(stored.KeyEncrypted);
                    }

                    var validator = http.RequestServices.GetRequiredService<ILlmKeyValidator>();
                    var models = await validator.ListModelsAsync(
                        request.Provider, request.BaseUrl ?? "", apiKey, http.RequestAborted);
                    return models is null
                        ? TypedResults.BadRequest(new ErrorResponse("invalid_key"))
                        : TypedResults.Ok(new LlmModelsResponse(models));
                })
            .WithName("listLlmModels").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapDelete("/api/orgs/{orgId:long}/llm-config",
                async Task<Results<NoContent, NotFound>> (
                    long orgId, SecretsConfig secrets, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var store = http.RequestServices.GetRequiredService<PostgresLlmConfigStore>();
                    return await store.DeleteAsync(orgId, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("deleteLlmConfig").WithTags("Conductor")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }
}
