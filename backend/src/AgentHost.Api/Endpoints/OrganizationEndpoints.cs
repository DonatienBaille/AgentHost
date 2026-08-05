using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var orgsApi = app.MapGroup("/api/organizations").WithTags("Organizations").RequireAuthorization();

        orgsApi.MapGet("/", ListOrganizations).WithName("ListOrganizations");
        orgsApi.MapGet("/{id}", GetOrganization).WithName("GetOrganization");
        // Normal signup goes through POST /api/auth/register (creates org + owner together);
        // this endpoint is for an existing owner provisioning an additional organization. The new
        // org is not readable through this API until someone authenticates *into* it, since every
        // read below is scoped to the caller's own org.
        orgsApi.MapPost("/", CreateOrganization).WithName("CreateOrganization").WithValidation<CreateOrganizationRequest>()
            .RequireAuthorization(AuthorizationPolicies.Owner);
        orgsApi.MapPut("/{id}", UpdateOrganization).WithName("UpdateOrganization")
            .RequireAuthorization(AuthorizationPolicies.Owner);
        orgsApi.MapDelete("/{id}", DeleteOrganization).WithName("DeleteOrganization")
            .RequireAuthorization(AuthorizationPolicies.Owner);

        return app;
    }

    /// <summary>
    /// Returns only the caller's own organization. This used to return every organization in the
    /// system — a full tenant roster handed to any authenticated user.
    /// </summary>
    private static async Task<IResult> ListOrganizations(
        IOrganizationRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var org = await repository.GetAsync(caller.OrgId, ct);
        return Results.Ok(org is null ? new List<Organization>() : new List<Organization> { org });
    }

    private static async Task<IResult> GetOrganization(
        string id, IOrganizationRepository repository, ICallerContext caller, CancellationToken ct)
    {
        if (!caller.BelongsToCallerOrg(id)) return Results.NotFound();

        var org = await repository.GetAsync(id, ct);
        return org != null ? Results.Ok(org) : Results.NotFound();
    }

    private static async Task<IResult> CreateOrganization(
        CreateOrganizationRequest req, IOrganizationRepository repository, ICallerContext caller, CancellationToken ct)
    {
        if (await repository.GetBySlugAsync(req.Slug, ct) is not null)
            return Results.Conflict(new { error = $"Organization slug '{req.Slug}' is already taken" });

        var now = DateTime.UtcNow;
        var org = new Organization
        {
            Id = UlidGenerator.NewUlid(),
            Name = req.Name,
            Slug = req.Slug,
            Plan = req.Plan,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.InsertAsync(org, ct);
        return Results.Created($"/api/organizations/{org.Id}", org);
    }

    private static async Task<IResult> UpdateOrganization(
        string id, UpdateOrganizationRequest req, IOrganizationRepository repository, ICallerContext caller, CancellationToken ct)
    {
        if (!caller.BelongsToCallerOrg(id)) return Results.NotFound();

        var org = await repository.GetAsync(id, ct);
        if (org is null) return Results.NotFound();

        if (req.Name is not null) org.Name = req.Name;
        if (req.Plan is not null) org.Plan = req.Plan;
        org.UpdatedAt = DateTime.UtcNow;

        await repository.UpdateAsync(org, ct);
        return Results.Ok(org);
    }

    /// <summary>
    /// Soft-deletes the organization and cascades to its projects, agents, runs, secrets and users
    /// in a single transaction. Deleting only the org row left every child live and readable.
    /// </summary>
    private static async Task<IResult> DeleteOrganization(
        string id, IOrganizationRepository repository, IAuditService auditService, ICallerContext caller, CancellationToken ct)
    {
        if (!caller.BelongsToCallerOrg(id)) return Results.NotFound();

        var org = await repository.GetAsync(id, ct);
        if (org is null) return Results.NotFound();

        // Audit first: the cascade is what we want on the record, and audit_log rows outlive the
        // org by design (WORM retention).
        await auditService.RecordAsync(id, "organization.deleted", caller.UserId, "organization", id, ct: ct);
        await repository.SoftDeleteCascadeAsync(id, ct);
        return Results.NoContent();
    }
}
