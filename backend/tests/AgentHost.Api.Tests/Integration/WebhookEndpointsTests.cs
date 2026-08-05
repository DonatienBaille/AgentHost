using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class WebhookEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public WebhookEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateUpdateDelete_RoundTrips_WithHardDelete()
    {
        var suffix = TestData.Suffix();
        var (client, _, project, _) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var createResponse = await client.PostJsonAsync("/api/webhooks", new CreateWebhookRequest
        {
            ProjectId = project.Id,
            Url = "https://example.invalid/webhook-sink",
            Events = new List<string> { "run.created" },
            SecretToken = "initial-secret",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var webhook = await createResponse.Content.ReadFromJsonAsync<Webhook>(TestJson.Options);
        Assert.NotNull(webhook);
        Assert.True(webhook!.IsActive);

        // PUT toggles isActive/url/events.
        var updateResponse = await client.PutJsonAsync($"/api/webhooks/{webhook.Id}", new UpdateWebhookRequest
        {
            Url = "https://example.invalid/webhook-sink-v2",
            Events = new List<string> { "run.created", "run.succeeded" },
            IsActive = false,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<Webhook>(TestJson.Options);
        Assert.NotNull(updated);
        Assert.Equal("https://example.invalid/webhook-sink-v2", updated!.Url);
        Assert.Equal(2, updated.Events.Count);
        Assert.False(updated.IsActive);

        var getResponse = await client.GetAsync($"/api/webhooks/{webhook.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<Webhook>(TestJson.Options);
        Assert.Equal("https://example.invalid/webhook-sink-v2", fetched!.Url);
        Assert.False(fetched.IsActive);

        // WebhookRepository.DeleteAsync is a hard DELETE (not soft delete like the other resources).
        var deleteResponse = await client.DeleteAsync($"/api/webhooks/{webhook.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeleteResponse = await client.GetAsync($"/api/webhooks/{webhook.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task CreateWebhook_RequiresMaintainer()
    {
        var suffix = TestData.Suffix();
        var (ownerClient, owner, project, _) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var (developerToken, _) = await TestData.CreateUserWithRoleAsync(ownerClient, owner.User.OrgId, UserRole.Developer, suffix);
        var developerClient = TestData.AuthedClient(_factory, developerToken);

        var req = new CreateWebhookRequest
        {
            ProjectId = project.Id,
            Url = "https://example.invalid/hook",
            Events = new List<string> { "run.created" },
        };

        var anonResponse = await _factory.CreateClient().PostJsonAsync("/api/webhooks", req);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

        var developerResponse = await developerClient.PostJsonAsync("/api/webhooks", req);
        Assert.Equal(HttpStatusCode.Forbidden, developerResponse.StatusCode);

        var ownerResponse = await ownerClient.PostJsonAsync("/api/webhooks", req);
        Assert.Equal(HttpStatusCode.Created, ownerResponse.StatusCode);
    }

    /// <summary>
    /// The most valuable test in this suite: spins up a real local HTTP listener as the webhook
    /// target (WebhookDispatcher makes a genuine outbound HttpClient call, so a TestServer-backed
    /// in-memory client can't be the target), registers a webhook with a known secret pointing at
    /// it, triggers a real run.created event via POST /api/runs, and independently recomputes the
    /// HMAC-SHA256 signature to verify the hand-rolled X-AgentHost-Signature header is correct.
    /// </summary>
    [Fact]
    public async Task RunCreated_DispatchesWebhook_WithCorrectHmacSignature()
    {
        var suffix = TestData.Suffix();
        var (client, _, project, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        const string secretToken = "wh-secret-token-for-hmac-verification";
        var received = new TaskCompletionSource<(string Body, string? Signature)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var listenerBuilder = WebApplication.CreateBuilder();
        listenerBuilder.Logging.ClearProviders();
        listenerBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var listenerApp = listenerBuilder.Build();
        listenerApp.MapPost("/sink", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var signature = ctx.Request.Headers["X-AgentHost-Signature"].FirstOrDefault();
            received.TrySetResult((body, signature));
            return Results.Ok();
        });
        await listenerApp.StartAsync();

        var addressFeature = listenerApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var baseAddress = addressFeature!.Addresses.First();

        var webhookResponse = await client.PostJsonAsync("/api/webhooks", new CreateWebhookRequest
        {
            ProjectId = project.Id,
            Url = $"{baseAddress}/sink",
            Events = new List<string> { "run.created" },
            SecretToken = secretToken,
        });
        Assert.Equal(HttpStatusCode.Created, webhookResponse.StatusCode);

        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);

        // Delivery is fire-and-forget from the server's perspective; poll with a timeout rather
        // than a fixed sleep.
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
        var completed = await Task.WhenAny(received.Task, timeoutTask);
        Assert.True(completed == received.Task, "Webhook delivery did not reach the local listener within 15s");

        var (body, signature) = await received.Task;
        Assert.False(string.IsNullOrEmpty(signature));
        Assert.StartsWith("sha256=", signature);

        var expectedHex = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretToken), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal($"sha256={expectedHex}", signature);

        // Sanity: the body really is the run.created event for this run.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("run.created", doc.RootElement.GetProperty("event").GetString());

        await listenerApp.StopAsync();
    }
}
