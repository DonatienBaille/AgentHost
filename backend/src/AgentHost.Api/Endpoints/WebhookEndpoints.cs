using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var webhooksApi = app.MapGroup("/api/webhooks").WithTags("Webhooks");

        webhooksApi.MapGet("/{id}", GetWebhook).WithName("GetWebhook");
        webhooksApi.MapGet("/", ListWebhooks).WithName("ListWebhooks");
        webhooksApi.MapPost("/", CreateWebhook).WithName("CreateWebhook").WithValidation<CreateWebhookRequest>();

        return app;
    }

    private static async Task<IResult> GetWebhook(string id, IWebhookRepository repository, CancellationToken ct)
    {
        var webhook = await repository.GetAsync(id, ct);
        return webhook != null ? Results.Ok(webhook) : Results.NotFound();
    }

    private static async Task<IResult> ListWebhooks(string projectId, IWebhookRepository repository, CancellationToken ct)
    {
        var webhooks = await repository.ListByProjectAsync(projectId, ct);
        return Results.Ok(webhooks);
    }

    private static async Task<IResult> CreateWebhook(CreateWebhookRequest req, IWebhookRepository repository, CancellationToken ct)
    {
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
}
