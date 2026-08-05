using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var orgsApi = app.MapGroup("/api/organizations").WithTags("Organizations");

        orgsApi.MapGet("/", ListOrganizations).WithName("ListOrganizations");
        orgsApi.MapGet("/{id}", GetOrganization).WithName("GetOrganization");
        orgsApi.MapPost("/", CreateOrganization).WithName("CreateOrganization").WithValidation<CreateOrganizationRequest>();

        return app;
    }

    private static async Task<IResult> ListOrganizations(IOrganizationRepository repository, CancellationToken ct)
    {
        var orgs = await repository.ListAsync(ct);
        return Results.Ok(orgs);
    }

    private static async Task<IResult> GetOrganization(string id, IOrganizationRepository repository, CancellationToken ct)
    {
        var org = await repository.GetAsync(id, ct);
        return org != null ? Results.Ok(org) : Results.NotFound();
    }

    private static async Task<IResult> CreateOrganization(CreateOrganizationRequest req, IOrganizationRepository repository, CancellationToken ct)
    {
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
}
