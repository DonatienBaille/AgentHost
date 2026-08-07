using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Les déclencheurs entrants, de bout en bout (feuille de route, lot 4).
///
/// <b>Pourquoi ces tests portent sur la vraie pile HTTP.</b> Le point sensible n'est pas la
/// vérification de signature — elle est prouvée isolément par <c>IncomingWebhookProtocolTests</c> —
/// mais son <b>câblage</b> : que le corps brut arrive intact jusqu'au HMAC, que l'endpoint soit
/// bien anonyme, que le refus soit un refus, et qu'aucun run ne parte quand il n'aurait pas dû. Un
/// double de service validerait la forme des appels sans jamais exercer ce qui compte.
///
/// <b>Le test le plus important est celui qui compte les runs.</b> Une signature refusée n'a de
/// valeur que si rien n'a été lancé derrière ; un 401 servi après le lancement du run serait pire
/// qu'inutile, il serait rassurant.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TriggerEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public TriggerEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    // ---- gestion ----

    [Fact]
    public async Task Creating_a_webhook_trigger_returns_its_secret_exactly_once()
    {
        var f = await FixtureAsync();

        var created = await CreateWebhookTriggerAsync(f);

        Assert.False(string.IsNullOrWhiteSpace(created.Secret));
        Assert.Equal($"/api/hooks/{created.Trigger.Id}", created.Trigger.WebhookPath);

        // Toute relecture ultérieure est muette sur le secret : un secret que l'API redonne à
        // volonté n'est plus protégé par le chiffrement au repos, il l'est par l'autorisation de
        // lecture — un cran plus faible, et un cran plus facile à perdre.
        var reread = await GetAsync<TriggerResponse>(f.Client, $"/api/triggers/{created.Trigger.Id}");
        var raw = await (await f.Client.GetAsync($"/api/triggers/{created.Trigger.Id}")).Content.ReadAsStringAsync();

        Assert.Equal(created.Trigger.Id, reread.Id);
        Assert.DoesNotContain(created.Secret!, raw);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_a_cron_trigger_computes_its_next_occurrence()
    {
        var f = await FixtureAsync();

        var created = await CreateCronTriggerAsync(f, "0 9 * * *", "Europe/Paris");

        // Aucun secret : rien à signer, personne à authentifier.
        Assert.Null(created.Secret);
        Assert.Null(created.Trigger.WebhookPath);
        Assert.Equal("Europe/Paris", created.Trigger.TimeZone);
        // L'échéance est calculée à l'écriture : c'est elle, indexée, que le planificateur
        // interroge, au lieu de réévaluer toutes les expressions de l'installation.
        Assert.NotNull(created.Trigger.NextRunAt);
        Assert.True(created.Trigger.NextRunAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData("pas une expression", "UTC")]
    [InlineData("* * * *", "UTC")]
    [InlineData("0 9 * * *", "Mars/Olympus_Mons")]
    public async Task An_unusable_schedule_is_refused_at_creation_with_a_reason(string cron, string timeZone)
    {
        var f = await FixtureAsync();

        var response = await f.Client.PostJsonAsync($"/api/projects/{f.Project.Id}/triggers", new CreateTriggerRequest
        {
            AgentId = f.Agent.Id,
            Name = "Planification impossible",
            Type = "cron",
            CronExpression = cron,
            TimeZone = timeZone,
        });

        // Refusé ici, et non au premier réveil du planificateur des heures plus tard, dans un
        // journal que personne ne lit.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_trigger_can_be_disabled_which_also_clears_its_schedule()
    {
        var f = await FixtureAsync();
        var created = await CreateCronTriggerAsync(f, "*/5 * * * *");

        var response = await f.Client.PatchAsync(
            $"/api/triggers/{created.Trigger.Id}",
            JsonContent.Create(new UpdateTriggerRequest { IsActive = false }));
        response.EnsureSuccessStatusCode();

        var updated = (await response.Content.ReadFromJsonAsync<TriggerResponse>(TestJson.Options))!;
        Assert.False(updated.IsActive);
        // Laisser l'échéance en place ferait repartir le déclencheur au réveil, avec toutes les
        // occurrences manquées d'un coup.
        Assert.Null(updated.NextRunAt);
    }

    [Fact]
    public async Task Only_a_maintainer_or_above_may_create_or_delete_a_trigger()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);

        foreach (var role in new[] { UserRole.Developer, UserRole.Viewer })
        {
            var (token, _) = await TestData.CreateUserWithRoleAsync(f.Client, role);
            using var client = TestData.AuthedClient(_factory, token);

            // Poser un déclencheur, c'est donner à un tiers le droit de dépenser le budget du
            // projet ; le lire n'engage rien, et reste ouvert à tous les rôles.
            var create = await client.PostJsonAsync($"/api/projects/{f.Project.Id}/triggers", new CreateTriggerRequest
            {
                AgentId = f.Agent.Id, Name = "Interdit", Type = "webhook", Provider = "github",
            });
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

            var delete = await client.DeleteAsync($"/api/triggers/{created.Trigger.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

            var read = await client.GetAsync($"/api/triggers/{created.Trigger.Id}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
    }

    [Fact]
    public async Task Another_organizations_triggers_are_invisible_and_untouchable()
    {
        var mine = await FixtureAsync();
        var theirs = await FixtureAsync();
        var theirTrigger = await CreateWebhookTriggerAsync(theirs);

        // Inexistant, jamais interdit : un 403 confirmerait l'existence de l'objet à qui ne le
        // connaît pas, ce qui suffit à cartographier une installation.
        Assert.Equal(HttpStatusCode.NotFound,
            (await mine.Client.GetAsync($"/api/triggers/{theirTrigger.Trigger.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await mine.Client.DeleteAsync($"/api/triggers/{theirTrigger.Trigger.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await mine.Client.GetAsync($"/api/projects/{theirs.Project.Id}/triggers")).StatusCode);

        // Et surtout : on ne pose pas un déclencheur sur l'agent d'autrui, ce qui reviendrait à
        // pouvoir lancer des runs sur son budget.
        var create = await mine.Client.PostJsonAsync($"/api/projects/{mine.Project.Id}/triggers", new CreateTriggerRequest
        {
            AgentId = theirs.Agent.Id, Name = "Détournement", Type = "webhook", Provider = "github",
        });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        // Le 404 ne suffit pas : il faut que RIEN n'ait été écrit. Une première version validait le
        // projet APRÈS l'insertion et rendait bien un 404 — en laissant derrière lui un déclencheur
        // parfaitement fonctionnel, avec son secret et son URL, sur l'agent d'un autre locataire.
        Assert.Empty(await ListTriggersAsync(mine));
        Assert.Single(await ListTriggersAsync(theirs));
    }

    [Fact]
    public async Task A_deleted_trigger_stops_answering_its_hook()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);

        (await f.Client.DeleteAsync($"/api/triggers/{created.Trigger.Id}")).EnsureSuccessStatusCode();

        var response = await PostSignedAsync(created, PushBody("main"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- ingestion ----

    [Fact]
    public async Task A_signed_delivery_launches_a_run_carrying_its_payload()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);

        var response = await PostSignedAsync(created, PushBody("main"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonNode>())?["runId"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var run = await LoadRunAsync(runId!);
        Assert.Equal(TriggeredByType.Webhook, run.TriggeredByType);
        // Aucun utilisateur derrière une livraison : l'acteur reste nul plutôt que d'emprunter
        // l'identité du créateur du déclencheur, qui n'a rien fait.
        Assert.Null(run.TriggeredByUserId);

        // Le contexte porte l'origine ET la charge utile : sans elles, un agent déclenché par un
        // push ignorerait quel push.
        Assert.Equal(created.Trigger.Id, run.Context?["trigger"]?["id"]?.GetValue<string>());
        Assert.Equal("push", run.Context?["trigger"]?["event"]?.GetValue<string>());
        Assert.Equal("main", run.Context?["trigger"]?["branch"]?.GetValue<string>());
        Assert.Equal("refs/heads/main", run.Context?["payload"]?["ref"]?.GetValue<string>());
    }

    [Fact]
    public async Task The_triggers_fixed_inputs_reach_the_run()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f, inputs: new JsonObject { ["mode"] = "nightly" });

        var response = await PostSignedAsync(created, PushBody("main"));
        var runId = (await response.Content.ReadFromJsonAsync<JsonNode>())!["runId"]!.GetValue<string>();

        // Un déclencheur n'a personne devant lui pour remplir un formulaire : ce qu'il ne porte
        // pas, le run ne l'a pas.
        var run = await LoadRunAsync(runId);
        Assert.Equal("nightly", run.Inputs?["mode"]?.GetValue<string>());
    }

    [Fact]
    public async Task An_unsigned_or_wrongly_signed_delivery_launches_nothing()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);
        var before = await CountRunsAsync(f);

        var unsigned = await PostRawAsync(created.Trigger.Id, PushBody("main"), signature: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var forged = await PostRawAsync(created.Trigger.Id, PushBody("main"),
            signature: Sign(PushBody("main"), "le-mauvais-secret"));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        // Le vrai test : un 401 servi APRÈS le lancement du run serait pire qu'inutile.
        Assert.Equal(before, await CountRunsAsync(f));
    }

    [Fact]
    public async Task A_signature_of_a_different_body_launches_nothing()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);
        var before = await CountRunsAsync(f);

        // Signature authentique, corps substitué : c'est exactement l'attaque que le HMAC sur les
        // octets bruts existe pour arrêter.
        var response = await PostRawAsync(created.Trigger.Id, PushBody("attacker"),
            signature: Sign(PushBody("main"), created.Secret!));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await CountRunsAsync(f));
    }

    [Fact]
    public async Task An_unknown_or_disabled_trigger_answers_the_same_as_a_missing_one()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);

        Assert.Equal(HttpStatusCode.NotFound,
            (await PostRawAsync(Infrastructure.UlidGenerator.NewUlid(), PushBody("main"), signature: null)).StatusCode);

        (await f.Client.PatchAsync($"/api/triggers/{created.Trigger.Id}",
            JsonContent.Create(new UpdateTriggerRequest { IsActive = false }))).EnsureSuccessStatusCode();

        // Désactivé et inconnu répondent pareil : distinguer les deux confirmerait l'existence
        // d'un déclencheur à qui ne le connaît pas — et le fait sans même exiger de signature.
        Assert.Equal(HttpStatusCode.NotFound, (await PostSignedAsync(created, PushBody("main"))).StatusCode);
    }

    [Fact]
    public async Task A_replayed_delivery_does_not_launch_a_second_run()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);
        var body = PushBody("main");

        var first = await PostSignedAsync(created, body, deliveryId: "delivery-1");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var after = await CountRunsAsync(f);

        var replay = await PostSignedAsync(created, body, deliveryId: "delivery-1");

        // 200 et non 4xx : GitHub réessaie sur 5xx et marque un 4xx en échec dans son interface.
        // La livraison a été correctement traitée, il n'y a simplement rien à refaire.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("duplicate", await replay.Content.ReadAsStringAsync());
        Assert.Equal(after, await CountRunsAsync(f));
    }

    [Fact]
    public async Task A_delivery_outside_the_filter_is_accepted_but_launches_nothing()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f, events: ["push"], branches: ["main"]);
        var before = await CountRunsAsync(f);

        var wrongBranch = await PostSignedAsync(created, PushBody("feature/x"), deliveryId: "d-branch");
        Assert.Equal(HttpStatusCode.OK, wrongBranch.StatusCode);
        Assert.Contains("filtered", await wrongBranch.Content.ReadAsStringAsync());

        var wrongEvent = await PostSignedAsync(created, PushBody("main"), eventName: "issues", deliveryId: "d-event");
        Assert.Equal(HttpStatusCode.OK, wrongEvent.StatusCode);

        Assert.Equal(before, await CountRunsAsync(f));

        // Et la livraison qui passe le filtre, elle, lance bien.
        var accepted = await PostSignedAsync(created, PushBody("main"), deliveryId: "d-ok");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_anything_is_computed()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);

        // L'endpoint est anonyme : sans borne, on y déverserait un flux que le processus garderait
        // en mémoire le temps d'en calculer le HMAC.
        var huge = Encoding.UTF8.GetBytes("{\"pad\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
        var response = await PostRawAsync(created.Trigger.Id, huge, signature: Sign(huge, created.Secret!));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task The_hook_needs_no_session_at_all()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);

        // Sans en-tête Authorization : c'est une forge qui appelle, elle n'a pas de compte ici.
        using var anonymous = _factory.CreateClient();
        var request = Signed(created.Trigger.Id, PushBody("main"), Sign(PushBody("main"), created.Secret!), "push", "d-anon");
        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Firing_a_trigger_leaves_an_audit_trail()
    {
        var f = await FixtureAsync();
        var created = await CreateWebhookTriggerAsync(f);
        await PostSignedAsync(created, PushBody("main"));

        var page = await GetAsync<AuditPage>(f.Client,
            $"/api/organizations/{f.Auth.User.OrgId}/audit-log?action=trigger.fired");

        var entry = Assert.Single(page.Items);
        Assert.Equal(created.Trigger.Id, entry.ResourceId);
        // Pas d'acteur : personne n'a cliqué. C'est le déclencheur qui a agi, et le dire vaut
        // mieux que d'attribuer le run à qui l'a configuré des mois plus tôt.
        Assert.Null(entry.ActorUserId);
        Assert.Equal("webhook", entry.Details?["triggeredBy"]?.GetValue<string>());
    }

    // ---- helpers ----

    private sealed record Fixture(HttpClient Client, AuthResponse Auth, Project Project, Agent Agent);

    private async Task<Fixture> FixtureAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        return new Fixture(client, auth, project, agent);
    }

    private async Task<CreateTriggerResponse> CreateWebhookTriggerAsync(
        Fixture f, List<string>? events = null, List<string>? branches = null, JsonNode? inputs = null)
    {
        var response = await f.Client.PostJsonAsync($"/api/projects/{f.Project.Id}/triggers", new CreateTriggerRequest
        {
            AgentId = f.Agent.Id,
            Name = "Push sur main",
            Type = "webhook",
            Provider = "github",
            Events = events,
            Branches = branches,
            Inputs = inputs,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateTriggerResponse>(TestJson.Options))!;
    }

    private async Task<CreateTriggerResponse> CreateCronTriggerAsync(
        Fixture f, string cron, string timeZone = "UTC")
    {
        var response = await f.Client.PostJsonAsync($"/api/projects/{f.Project.Id}/triggers", new CreateTriggerRequest
        {
            AgentId = f.Agent.Id,
            Name = "Rapport quotidien",
            Type = "cron",
            CronExpression = cron,
            TimeZone = timeZone,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateTriggerResponse>(TestJson.Options))!;
    }

    private static byte[] PushBody(string branch) =>
        Encoding.UTF8.GetBytes($$"""{"ref":"refs/heads/{{branch}}","after":"0123456789abcdef"}""");

    private static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    private static HttpRequestMessage Signed(
        string triggerId, byte[] body, string? signature, string eventName, string deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/hooks/{triggerId}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null)
            request.Headers.TryAddWithoutValidation(IncomingWebhookProtocol.GitHubSignatureHeader, signature);
        request.Headers.TryAddWithoutValidation(IncomingWebhookProtocol.GitHubEventHeader, eventName);
        request.Headers.TryAddWithoutValidation(IncomingWebhookProtocol.GitHubDeliveryHeader, deliveryId);
        return request;
    }

    private async Task<HttpResponseMessage> PostSignedAsync(
        CreateTriggerResponse trigger, byte[] body, string eventName = "push", string? deliveryId = null)
    {
        using var client = _factory.CreateClient();
        return await client.SendAsync(Signed(
            trigger.Trigger.Id, body, Sign(body, trigger.Secret!), eventName,
            deliveryId ?? Guid.NewGuid().ToString("N")));
    }

    private async Task<HttpResponseMessage> PostRawAsync(string triggerId, byte[] body, string? signature)
    {
        using var client = _factory.CreateClient();
        return await client.SendAsync(Signed(triggerId, body, signature, "push", Guid.NewGuid().ToString("N")));
    }

    /// <summary>Les déclencheurs réellement en base pour ce projet, vus par leur propriétaire.</summary>
    private static async Task<List<TriggerResponse>> ListTriggersAsync(Fixture f) =>
        await GetAsync<List<TriggerResponse>>(f.Client, $"/api/projects/{f.Project.Id}/triggers");

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(TestJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>Relit un run par le dépôt : le but est son contexte et son déclencheur, pas son statut.</summary>
    private async Task<Run> LoadRunAsync(string runId)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        return await runs.GetAsync(runId) ?? throw new InvalidOperationException($"Run {runId} not found");
    }

    private async Task<int> CountRunsAsync(Fixture f)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        return (await runs.ListByProjectAsync(f.Project.Id, f.Auth.User.OrgId, 0, 200)).Count;
    }
}
