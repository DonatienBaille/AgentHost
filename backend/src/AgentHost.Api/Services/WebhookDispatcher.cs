using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHost.Api.Repositories;
using Polly;
using Serilog;

namespace AgentHost.Api.Services;

public interface IWebhookDispatcher
{
    /// <summary>
    /// Fire-and-forget dispatch of an event to every active webhook subscribed to it for the
    /// given project. Never throws; a failing/slow webhook is logged and does not block the
    /// caller or any other webhook.
    /// </summary>
    Task DispatchAsync(string projectId, string eventName, object payload, CancellationToken ct = default);
}

/// <summary>
/// Posts webhook payloads (spec: webhooks table, events jsonb array) to subscriber URLs. Each
/// delivery is retried a couple of times with exponential backoff via Polly, and signed with
/// HMAC-SHA256 when the webhook has a secret_token configured.
/// </summary>
public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly IWebhookRepository _webhookRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public WebhookDispatcher(IWebhookRepository webhookRepository, IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _webhookRepository = webhookRepository;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DispatchAsync(string projectId, string eventName, object payload, CancellationToken ct = default)
    {
        List<Domain.Webhook> webhooks;
        try
        {
            webhooks = await _webhookRepository.ListActiveForEventAsync(projectId, eventName, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to look up webhooks for project {ProjectId} event {EventName}", projectId, eventName);
            return;
        }

        if (webhooks.Count == 0) return;

        var body = JsonSerializer.Serialize(new
        {
            @event = eventName,
            timestamp = DateTime.UtcNow,
            data = payload,
        });

        foreach (var webhook in webhooks)
        {
            // Fire-and-forget per webhook, deliberately decoupled from the caller's
            // CancellationToken/DI scope: a slow/failing endpoint must not block others, and
            // delivery should not abort just because the originating HTTP request completed.
            _ = DeliverAsync(webhook, body, CancellationToken.None);
        }
    }

    private async Task DeliverAsync(Domain.Webhook webhook, string body, CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(2, i => TimeSpan.FromSeconds(Math.Pow(2, i)));

        try
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                using var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrEmpty(webhook.SecretToken))
                {
                    var signature = ComputeSignature(webhook.SecretToken, body);
                    request.Headers.Add("X-AgentHost-Signature", $"sha256={signature}");
                }

                var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Webhook {webhook.Id} returned {(int)response.StatusCode}");
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to deliver webhook {WebhookId} to {Url}", webhook.Id, webhook.Url);
        }
    }

    private static string ComputeSignature(string secret, string body)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(keyBytes, bodyBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
