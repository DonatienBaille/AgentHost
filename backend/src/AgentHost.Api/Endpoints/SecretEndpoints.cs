using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Secret metadata management, scoped to the caller's organization. Values are write-only over
/// this API: neither the plaintext nor the stored ciphertext (nor the vault path) is ever
/// returned, on any route — every response is projected through <see cref="SecretResponse"/>
/// rather than serializing the <see cref="Secret"/> entity, so a new column can't leak by
/// accident. Plaintext is only ever handed to a run's container by <see cref="ISecretsBroker"/>.
/// </summary>
public static class SecretEndpoints
{
    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var secretsApi = app.MapGroup("/api/secrets").WithTags("Secrets").RequireAuthorization();

        secretsApi.MapGet("/", ListSecrets).WithName("ListSecrets")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        secretsApi.MapGet("/{id}", GetSecret).WithName("GetSecret")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        secretsApi.MapPost("/", CreateSecret).WithName("CreateSecret").WithValidation<CreateSecretRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        secretsApi.MapPut("/{id}", RotateSecret).WithName("RotateSecret").WithValidation<UpdateSecretRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        secretsApi.MapDelete("/{id}", DeleteSecret).WithName("DeleteSecret")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        return app;
    }

    private static async Task<IResult> ListSecrets(
        ISecretRepository repository, ICallerContext caller, CancellationToken ct, int skip = 0, int take = 100)
    {
        var secrets = await repository.ListByOrgAsync(caller.OrgId, Paging.ClampSkip(skip), Paging.ClampTake(take), ct);
        return Results.Ok(secrets.Select(SecretResponse.From).ToList());
    }

    private static async Task<IResult> GetSecret(string id, ISecretRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var secret = await repository.GetAsync(id, caller.OrgId, ct);
        return secret is not null ? Results.Ok(SecretResponse.From(secret)) : Results.NotFound();
    }

    private static async Task<IResult> CreateSecret(
        CreateSecretRequest req,
        ISecretsBroker secretsBroker,
        ISecretRepository repository,
        IProjectRepository projectRepository,
        IAuditService auditService,
        ICallerContext caller,
        CancellationToken ct)
    {
        // A project-scoped secret must be attached to one of the caller's own projects.
        if (!string.IsNullOrEmpty(req.ProjectId) &&
            await projectRepository.GetAsync(req.ProjectId, caller.OrgId, ct) is null)
        {
            return Results.NotFound(new { error = "Project not found" });
        }

        var now = DateTime.UtcNow;
        var secret = new Secret
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = caller.OrgId,
            ProjectId = req.ProjectId,
            Name = req.Name,
            EncryptedValue = secretsBroker.Encrypt(req.Value),
            Scope = req.Scope,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.InsertAsync(secret, ct);
        await auditService.RecordAsync(caller.OrgId, "secret.created", caller.UserId, "secret", secret.Id, ct: ct);

        return Results.Created($"/api/secrets/{secret.Id}", SecretResponse.From(secret));
    }

    /// <summary>Re-encrypts the secret with a new value. The value is never echoed back.</summary>
    private static async Task<IResult> RotateSecret(
        string id,
        UpdateSecretRequest req,
        ISecretsBroker secretsBroker,
        ISecretRepository repository,
        IAuditService auditService,
        ICallerContext caller,
        CancellationToken ct)
    {
        var secret = await repository.GetAsync(id, caller.OrgId, ct);
        if (secret is null) return Results.NotFound();

        secret.EncryptedValue = secretsBroker.Encrypt(req.Value);
        secret.UpdatedAt = DateTime.UtcNow;

        await repository.UpdateAsync(secret, ct);
        await auditService.RecordAsync(caller.OrgId, "secret.rotated", caller.UserId, "secret", secret.Id, ct: ct);

        return Results.Ok(SecretResponse.From(secret));
    }

    private static async Task<IResult> DeleteSecret(
        string id, ISecretRepository repository, IAuditService auditService, ICallerContext caller, CancellationToken ct)
    {
        var secret = await repository.GetAsync(id, caller.OrgId, ct);
        if (secret is null) return Results.NotFound();

        await repository.SoftDeleteAsync(id, caller.OrgId, ct);
        await auditService.RecordAsync(caller.OrgId, "secret.deleted", caller.UserId, "secret", id, ct: ct);
        return Results.NoContent();
    }
}
