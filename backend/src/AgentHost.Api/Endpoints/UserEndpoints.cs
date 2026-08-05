using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
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
        usersApi.MapPut("/{id}", UpdateUser).WithName("UpdateUser").WithValidation<UpdateUserRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        usersApi.MapDelete("/{id}", DeleteUser).WithName("DeleteUser")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        return app;
    }

    private static async Task<IResult> GetUser(string id, IUserRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var user = await repository.GetAsync(id, caller.OrgId, ct);
        return user != null ? Results.Ok(user) : Results.NotFound();
    }

    /// <summary>
    /// Lists the caller's own organization's members. The org id used to come from the query
    /// string, which let any authenticated user enumerate every other tenant's user directory
    /// (emails, roles) by guessing an org ULID.
    /// </summary>
    private static async Task<IResult> ListUsers(IUserRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var users = await repository.ListByOrgAsync(caller.OrgId, ct);
        return Results.Ok(users);
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest req, IUserRepository repository, IAuditService auditService, ICallerContext caller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = caller.OrgId,
            Email = req.Email,
            DisplayName = req.DisplayName,
            Role = req.Role,
            PasswordHash = PasswordHasher.Hash(req.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.InsertAsync(user, ct);
        await auditService.RecordAsync(caller.OrgId, "user.created", caller.UserId, "user", user.Id, ct: ct);
        return Results.Created($"/api/users/{user.Id}", user);
    }

    private static async Task<IResult> UpdateUser(
        string id, UpdateUserRequest req, IUserRepository repository, IAuditService auditService, ICallerContext caller, CancellationToken ct)
    {
        var user = await repository.GetAsync(id, caller.OrgId, ct);
        if (user is null) return Results.NotFound();

        if (req.DisplayName is not null) user.DisplayName = req.DisplayName;
        if (req.Role is not null) user.Role = req.Role.Value;
        if (!string.IsNullOrEmpty(req.Password)) user.PasswordHash = PasswordHasher.Hash(req.Password);
        user.UpdatedAt = DateTime.UtcNow;

        await repository.UpdateAsync(user, ct);
        await auditService.RecordAsync(caller.OrgId, "user.updated", caller.UserId, "user", user.Id, ct: ct);
        return Results.Ok(user);
    }

    private static async Task<IResult> DeleteUser(
        string id,
        IUserRepository repository,
        IRefreshTokenRepository refreshTokenRepository,
        IAuditService auditService,
        ICallerContext caller,
        CancellationToken ct)
    {
        var user = await repository.GetAsync(id, caller.OrgId, ct);
        if (user is null) return Results.NotFound();

        await repository.SoftDeleteAsync(id, caller.OrgId, ct);
        // A deleted user must not be able to mint fresh access tokens from a refresh token they
        // still hold. (Their current access token stays valid until it expires — by design.)
        await refreshTokenRepository.RevokeAllForUserAsync(id, ct);
        await auditService.RecordAsync(caller.OrgId, "user.deleted", caller.UserId, "user", id, ct: ct);
        return Results.NoContent();
    }
}
