using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Webhook CRUD, scoped to the caller's organization through the owning project. Webhooks carry a
/// signing secret and a delivery URL, so cross-tenant access here would both leak the HMAC secret
/// and let an attacker redirect another tenant's run events to a host of their choosing.
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var webhooksApi = app.MapGroup("/api/webhooks").WithTags("Webhooks").RequireAuthorization();

        webhooksApi.MapGet("/{id}", GetWebhook).WithName("GetWebhook");
        webhooksApi.MapGet("/", ListWebhooks).WithName("ListWebhooks");
        webhooksApi.MapPost("/", CreateWebhook).WithName("CreateWebhook").WithValidation<CreateWebhookRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        webhooksApi.MapPut("/{id}", UpdateWebhook).WithName("UpdateWebhook")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        webhooksApi.MapDelete("/{id}", DeleteWebhook).WithName("DeleteWebhook")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        return app;
    }

    private static async Task<IResult> GetWebhook(string id, IWebhookRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var webhook = await repository.GetAsync(id, caller.OrgId, ct);
        return webhook != null ? Results.Ok(webhook) : Results.NotFound();
    }

    private static async Task<IResult> ListWebhooks(
        string projectId, IWebhookRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var webhooks = await repository.ListByProjectAsync(projectId, caller.OrgId, ct);
        return Results.Ok(webhooks);
    }

    private static async Task<IResult> CreateWebhook(
        CreateWebhookRequest req,
        IWebhookRepository repository,
        IProjectRepository projectRepository,
        ICallerContext caller,
        CancellationToken ct)
    {
        if (await projectRepository.GetAsync(req.ProjectId, caller.OrgId, ct) is null)
            return Results.NotFound(new { error = "Project not found" });

        var now = DateTime.UtcNow;
        var webhook = new Webhook
        {
            Id = UlidGenerator.NewUlid(),
            ProjectId = req.ProjectId,
            Url = req.Url,
            Events = req.Events,
            SecretToken = req.SecretToken,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.InsertAsync(webhook, ct);
        return Results.Created($"/api/webhooks/{webhook.Id}", webhook);
    }

    private static async Task<IResult> UpdateWebhook(
        string id, UpdateWebhookRequest req, IWebhookRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var webhook = await repository.GetAsync(id, caller.OrgId, ct);
        if (webhook is null) return Results.NotFound();

        if (req.Url is not null) webhook.Url = req.Url;
        if (req.Events is not null) webhook.Events = req.Events;
        if (req.SecretToken is not null) webhook.SecretToken = req.SecretToken;
        if (req.IsActive is not null) webhook.IsActive = req.IsActive.Value;
        webhook.UpdatedAt = DateTime.UtcNow;

        await repository.UpdateAsync(webhook, ct);
        return Results.Ok(webhook);
    }

    private static async Task<IResult> DeleteWebhook(string id, IWebhookRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var webhook = await repository.GetAsync(id, caller.OrgId, ct);
        if (webhook is null) return Results.NotFound();

        await repository.DeleteAsync(id, caller.OrgId, ct);
        return Results.NoContent();
    }
}
