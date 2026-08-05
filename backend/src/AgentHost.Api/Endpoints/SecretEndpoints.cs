using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Secret metadata management. Plaintext values are never returned once stored — only
/// existence/metadata (name, scope, last used) is exposed via the API.
/// </summary>
public static class SecretEndpoints
{
    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var secretsApi = app.MapGroup("/api/secrets").WithTags("Secrets").RequireAuthorization();

        secretsApi.MapPost("/", CreateSecret).WithName("CreateSecret").WithValidation<CreateSecretRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        return app;
    }

    private static async Task<IResult> CreateSecret(
        CreateSecretRequest req, ISecretsBroker secretsBroker, ISecretRepository repository, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var secret = new Secret
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = req.OrgId,
            ProjectId = req.ProjectId,
            Name = req.Name,
            EncryptedValue = secretsBroker.Encrypt(req.Value),
            Scope = req.Scope,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.InsertAsync(secret, ct);

        // Never echo the plaintext or ciphertext back.
        return Results.Created($"/api/secrets/{secret.Id}", new
        {
            secret.Id,
            secret.OrgId,
            secret.ProjectId,
            secret.Name,
            Scope = secret.Scope.ToDbString(),
            secret.CreatedAt,
        });
    }
}
