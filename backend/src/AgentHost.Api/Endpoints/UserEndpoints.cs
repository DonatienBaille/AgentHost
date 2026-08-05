using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var usersApi = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization();

        usersApi.MapGet("/{id}", GetUser).WithName("GetUser");
        usersApi.MapGet("/", ListUsers).WithName("ListUsers");
        usersApi.MapPost("/", CreateUser).WithName("CreateUser").WithValidation<CreateUserRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        return app;
    }

    private static async Task<IResult> GetUser(string id, IUserRepository repository, CancellationToken ct)
    {
        var user = await repository.GetAsync(id, ct);
        return user != null ? Results.Ok(user) : Results.NotFound();
    }

    private static async Task<IResult> ListUsers(string orgId, IUserRepository repository, CancellationToken ct)
    {
        var users = await repository.ListByOrgAsync(orgId, ct);
        return Results.Ok(users);
    }

    private static async Task<IResult> CreateUser(CreateUserRequest req, IUserRepository repository, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = req.OrgId,
            Email = req.Email,
            DisplayName = req.DisplayName,
            Role = req.Role,
            PasswordHash = PasswordHasher.Hash(req.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.InsertAsync(user, ct);
        return Results.Created($"/api/users/{user.Id}", user);
    }
}
