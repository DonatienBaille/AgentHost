using System.Text.Json.Nodes;
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
        // La cible n'est résolue nulle part : la consultation du journal joint l'ACTEUR à la table
        // des utilisateurs, pas la ressource. Sans ces deux champs, « user.created 01HZX… » ne dit
        // pas quel compte a été ouvert, ni avec quel pouvoir.
        await auditService.RecordAsync(
            caller.OrgId, "user.created", caller.UserId, "user", user.Id,
            details: new JsonObject { ["email"] = user.Email, ["role"] = user.Role.ToDbString() },
            ct: ct);
        return Results.Created($"/api/users/{user.Id}", user);
    }

    private static async Task<IResult> UpdateUser(
        string id, UpdateUserRequest req, IUserRepository repository, IAuditService auditService, ICallerContext caller, CancellationToken ct)
    {
        var user = await repository.GetAsync(id, caller.OrgId, ct);
        if (user is null) return Results.NotFound();

        var previousRole = user.Role;
        var previousDisplayName = user.DisplayName;

        if (req.DisplayName is not null) user.DisplayName = req.DisplayName;
        if (req.Role is not null) user.Role = req.Role.Value;
        if (!string.IsNullOrEmpty(req.Password)) user.PasswordHash = PasswordHasher.Hash(req.Password);
        user.UpdatedAt = DateTime.UtcNow;

        await repository.UpdateAsync(user, ct);

        // La colonne `changes` existait depuis l'origine et personne ne l'alimentait : le journal
        // savait dire QUE ce compte avait été modifié, jamais EN QUOI. Or l'élévation de privilège
        // est précisément l'événement pour lequel on tient un journal d'audit — « qui m'a donné le
        // rôle owner, et quand » n'a pas de réponse si l'entrée ne porte que l'identifiant.
        //
        // Le mot de passe fait exception et n'est noté que comme rotation : ni l'ancienne valeur ni
        // la nouvelle n'ont à figurer dans une table que le journal rend consultable à tout
        // mainteneur, et l'empreinte n'y apprendrait rien à personne.
        var changes = new JsonObject();
        if (req.Role is not null && req.Role.Value != previousRole)
            changes["role"] = new JsonObject { ["from"] = previousRole.ToDbString(), ["to"] = user.Role.ToDbString() };
        if (req.DisplayName is not null && req.DisplayName != previousDisplayName)
            changes["displayName"] = new JsonObject { ["from"] = previousDisplayName, ["to"] = user.DisplayName };
        if (!string.IsNullOrEmpty(req.Password))
            changes["password"] = "rotated";

        await auditService.RecordAsync(
            caller.OrgId, "user.updated", caller.UserId, "user", user.Id,
            // Une requête qui ne change rien laisse une entrée sans `changes` plutôt qu'un objet
            // vide : « rien n'a bougé » se lit mieux que « {} ».
            changes: changes.Count > 0 ? changes : null,
            ct: ct);
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
        await auditService.RecordAsync(
            caller.OrgId, "user.deleted", caller.UserId, "user", id,
            details: new JsonObject { ["email"] = user.Email, ["role"] = user.Role.ToDbString() },
            ct: ct);
        return Results.NoContent();
    }
}
