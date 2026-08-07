using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// La consultation filtrée du journal d'audit (feuille de route, lot 3).
///
/// <b>Pourquoi ces tests portent sur la vraie base.</b> Le filtrage est du SQL construit
/// dynamiquement : un prédicat oublié, une borne inclusive du mauvais côté ou une jointure qui
/// multiplie les lignes produisent une page parfaitement plausible et fausse. Rien de tout cela ne
/// se voit à la lecture, et un double de dépôt validerait la forme des appels sans jamais exécuter
/// la requête qui compte.
///
/// <b>Chaque test part d'une organisation neuve</b>, dont le journal est vide : l'inscription
/// n'écrit aucune entrée. Les totaux attendus sont donc exacts et non « au moins n », ce qui est la
/// différence entre un test qui attrape une fuite et un test qui la tolère.
///
/// Le test qui compte le plus reste celui de l'isolation : un journal d'audit contient les
/// identifiants d'utilisateurs, les ressources et l'activité complète d'un locataire.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuditBrowsingTests
{
    private readonly AgentHostApiFactory _factory;

    public AuditBrowsingTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_empty_log_is_an_empty_page_and_not_an_error()
    {
        var f = await FixtureAsync();

        var page = await GetPageAsync(f);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        // La page réelle est renvoyée telle qu'appliquée : l'IHM affiche « 0 sur 0 » sans deviner.
        Assert.Equal(0, page.Skip);
        Assert.Equal(50, page.Take);
    }

    [Fact]
    public async Task Entries_come_back_newest_first()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "run.created", ageMinutes: 30);
        await SeedAsync(f, "secret.rotated", ageMinutes: 10);
        await SeedAsync(f, "user.deleted", ageMinutes: 60);

        var page = await GetPageAsync(f);

        // Un journal se lit du plus récent au plus ancien ; tout autre ordre oblige à paginer
        // jusqu'au bout pour voir ce qui vient de se produire.
        Assert.Equal(["secret.rotated", "run.created", "user.deleted"], page.Items.Select(e => e.Action));
    }

    [Fact]
    public async Task The_total_counts_the_filtered_set_and_not_the_page()
    {
        var f = await FixtureAsync();
        for (var i = 0; i < 5; i++) await SeedAsync(f, "run.created", ageMinutes: i);

        var page = await GetPageAsync(f, "?take=2");

        Assert.Equal(2, page.Items.Count);
        // Sans ce total, une pagination ne sait pas qu'il reste des pages autrement qu'en en
        // demandant une vide, et ne peut annoncer ni « 1–2 sur 5 » ni la dernière page.
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task Paging_walks_the_whole_log_without_repeating_or_skipping_an_entry()
    {
        var f = await FixtureAsync();
        for (var i = 0; i < 7; i++) await SeedAsync(f, "run.created", ageMinutes: i);

        var first = await GetPageAsync(f, "?take=3&skip=0");
        var second = await GetPageAsync(f, "?take=3&skip=3");
        var third = await GetPageAsync(f, "?take=3&skip=6");

        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(e => e.Id).ToList();
        Assert.Equal(7, seen.Count);
        // Le tri secondaire sur l'identifiant existe pour ça : deux entrées de la même
        // milliseconde départagées au hasard réapparaîtraient d'une page à l'autre.
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Filtering_by_action_narrows_the_page_and_its_total()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "secret.created");
        await SeedAsync(f, "secret.created");
        await SeedAsync(f, "run.created");

        var page = await GetPageAsync(f, "?action=secret.created");

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, e => Assert.Equal("secret.created", e.Action));
    }

    [Fact]
    public async Task Filtering_by_actor_answers_what_did_this_person_do()
    {
        var f = await FixtureAsync();
        var (_, other) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Developer);

        await SeedAsync(f, "run.created", actorUserId: f.Auth.User.Id);
        await SeedAsync(f, "run.cancelled", actorUserId: other.Id);
        await SeedAsync(f, "secret.deleted", actorUserId: other.Id);

        var page = await GetPageAsync(f, $"?actorUserId={other.Id}");

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, e => Assert.Equal(other.Id, e.ActorUserId));
    }

    [Fact]
    public async Task Filtering_by_resource_isolates_one_object_history()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "run.created", resourceType: "run", resourceId: "run-a");
        await SeedAsync(f, "run.cancelled", resourceType: "run", resourceId: "run-a");
        await SeedAsync(f, "run.created", resourceType: "run", resourceId: "run-b");
        await SeedAsync(f, "secret.created", resourceType: "secret", resourceId: "run-a");

        Assert.Equal(3, (await GetPageAsync(f, "?resourceType=run")).Total);
        // Le type ET l'identifiant : « run-a » existe aussi comme identifiant de secret, et les
        // confondre mélangerait deux objets sans rapport.
        Assert.Equal(2, (await GetPageAsync(f, "?resourceType=run&resourceId=run-a")).Total);
    }

    [Fact]
    public async Task Filters_combine_as_a_conjunction()
    {
        var f = await FixtureAsync();
        var (_, other) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Developer);

        await SeedAsync(f, "run.created", actorUserId: f.Auth.User.Id);
        await SeedAsync(f, "run.created", actorUserId: other.Id);
        await SeedAsync(f, "run.cancelled", actorUserId: other.Id);

        var page = await GetPageAsync(f, $"?action=run.created&actorUserId={other.Id}");

        // Une union rendrait trois entrées et donnerait l'illusion d'un filtre qui ne filtre rien.
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task The_period_includes_its_lower_bound_and_excludes_its_upper_one()
    {
        var f = await FixtureAsync();
        var noon = new DateTime(2031, 3, 3, 12, 0, 0, DateTimeKind.Utc);

        await SeedAsync(f, "before", at: noon.AddDays(-1));
        await SeedAsync(f, "start", at: new DateTime(2031, 3, 3, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(f, "middle", at: noon);
        await SeedAsync(f, "end", at: new DateTime(2031, 3, 4, 0, 0, 0, DateTimeKind.Utc));

        var page = await GetPageAsync(f, "?from=2031-03-03T00:00:00Z&to=2031-03-04T00:00:00Z");

        // « la journée du 3 » se demande [03-03, 04-03[ : la borne basse incluse rattrape l'entrée
        // de minuit pile, la borne haute exclue évite de compter deux fois celle du lendemain à
        // minuit — et surtout, personne n'a à écrire 23:59:59.999.
        Assert.Equal(["middle", "start"], page.Items.Select(e => e.Action));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task A_period_bound_without_a_timezone_is_read_as_utc()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "inside", at: new DateTime(2031, 5, 10, 1, 0, 0, DateTimeKind.Utc));

        // `created_at` est un TIMESTAMP sans fuseau alimenté en UTC : une borne nue interprétée
        // dans le fuseau du serveur décalerait la fenêtre sans que rien ne le signale.
        var page = await GetPageAsync(f, "?from=2031-05-10T00:00:00&to=2031-05-10T02:00:00");

        Assert.Equal(1, page.Total);
        Assert.Equal("inside", page.Items[0].Action);
    }

    [Fact]
    public async Task The_actor_is_resolved_to_a_name_because_a_ulid_identifies_nobody()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "run.created", actorUserId: f.Auth.User.Id);
        // Une entrée système n'a pas d'acteur : elle ne doit ni disparaître de la page ni faire
        // échouer la jointure.
        await SeedAsync(f, "password_reset.requested", actorUserId: null);

        var page = await GetPageAsync(f);

        Assert.Equal(2, page.Total);
        var identified = Assert.Single(page.Items, e => e.ActorUserId is not null);
        Assert.Equal(f.Auth.User.Email, identified.ActorEmail);
        Assert.Equal(f.Auth.User.DisplayName, identified.ActorDisplayName);

        var system = Assert.Single(page.Items, e => e.ActorUserId is null);
        Assert.Null(system.ActorEmail);
    }

    [Fact]
    public async Task A_deleted_account_is_still_named_in_the_log()
    {
        var f = await FixtureAsync();
        var (_, gone) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Developer);
        await SeedAsync(f, "secret.deleted", actorUserId: gone.Id);

        var delete = await f.Client.DeleteAsync($"/api/users/{gone.Id}");
        delete.EnsureSuccessStatusCode();

        var page = await GetPageAsync(f, "?action=secret.deleted");

        // Un journal d'audit sert justement à identifier après coup : la jointure ignore
        // `deleted_at`, sinon l'acteur le plus intéressant serait le seul à redevenir anonyme.
        Assert.Equal(gone.Email, Assert.Single(page.Items).ActorEmail);
    }

    [Fact]
    public async Task The_jsonb_payloads_survive_the_round_trip()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "user.updated",
            changes: new JsonObject { ["role"] = new JsonObject { ["from"] = "viewer", ["to"] = "owner" } },
            details: new JsonObject { ["ip"] = "10.0.0.1" });

        var entry = Assert.Single((await GetPageAsync(f)).Items);

        Assert.Equal("viewer", entry.Changes?["role"]?["from"]?.GetValue<string>());
        Assert.Equal("owner", entry.Changes?["role"]?["to"]?.GetValue<string>());
        Assert.Equal("10.0.0.1", entry.Details?["ip"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_role_change_records_what_it_changed_and_never_the_password()
    {
        var f = await FixtureAsync();
        var (_, target) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Viewer);

        var update = await f.Client.PutJsonAsync($"/api/users/{target.Id}",
            new UpdateUserRequest { Role = UserRole.Maintainer, Password = "another-correct-horse-battery" });
        update.EnsureSuccessStatusCode();

        var entry = Assert.Single((await GetPageAsync(f, "?action=user.updated")).Items);

        // L'élévation de privilège est l'événement pour lequel on tient un journal d'audit ; sans
        // ces deux valeurs, l'entrée dit qu'un compte a bougé mais pas vers quel pouvoir.
        Assert.Equal("viewer", entry.Changes?["role"]?["from"]?.GetValue<string>());
        Assert.Equal("maintainer", entry.Changes?["role"]?["to"]?.GetValue<string>());

        // La rotation est notée, la valeur ne l'est pas : le journal est lisible par tout
        // mainteneur de l'organisation.
        Assert.Equal("rotated", entry.Changes?["password"]?.GetValue<string>());
        Assert.DoesNotContain("another-correct-horse-battery", entry.Changes!.ToJsonString());
    }

    [Fact]
    public async Task An_update_that_changes_nothing_records_no_changes_rather_than_an_empty_object()
    {
        var f = await FixtureAsync();
        var (_, target) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Viewer);

        var update = await f.Client.PutJsonAsync($"/api/users/{target.Id}",
            new UpdateUserRequest { Role = UserRole.Viewer });
        update.EnsureSuccessStatusCode();

        Assert.Null(Assert.Single((await GetPageAsync(f, "?action=user.updated")).Items).Changes);
    }

    [Fact]
    public async Task Facets_report_what_the_log_actually_contains()
    {
        var f = await FixtureAsync();
        // Créer ce compte écrit une entrée `user.created` que personne n'a demandée ici : c'est
        // justement ce que les facettes doivent refléter — ce que le journal contient, pas ce que
        // le test croit y avoir mis.
        var (_, other) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Developer);

        for (var i = 0; i < 3; i++)
            await SeedAsync(f, "run.created", actorUserId: other.Id, resourceType: "run", ageMinutes: 5 + i);
        await SeedAsync(f, "secret.rotated", actorUserId: f.Auth.User.Id, resourceType: "secret", ageMinutes: 3);
        await SeedAsync(f, "password_reset.requested", actorUserId: null, ageMinutes: 2);

        var facets = await GetFacetsAsync(f);

        // Volume décroissant d'abord — la plus fréquente est la plus probable — puis ordre
        // alphabétique, pour qu'un rafraîchissement ne réordonne pas la liste déroulante.
        Assert.Equal(["run.created", "password_reset.requested", "secret.rotated", "user.created"],
            facets.Actions.Select(a => a.Value));
        Assert.Equal(3, facets.Actions[0].Count);

        // Une entrée sans ressource n'invente pas un type vide.
        Assert.Equal(["run", "secret", "user"], facets.ResourceTypes.Select(r => r.Value).Order());

        // L'acteur est nommé ici aussi : une liste déroulante de ULID ne serait pas un filtre.
        var top = facets.Actors.First();
        Assert.Equal(other.Id, top.UserId);
        Assert.Equal(other.Email, top.Email);
        Assert.Equal(3, top.Count);
        // Deux acteurs et non trois : l'entrée système sans acteur ne produit pas de ligne anonyme.
        Assert.Equal(2, facets.Actors.Count);

        Assert.Equal(6, facets.TotalEntries);
        Assert.NotNull(facets.EarliestEntry);
    }

    [Fact]
    public async Task An_empty_log_has_empty_facets_and_no_earliest_entry()
    {
        var facets = await GetFacetsAsync(await FixtureAsync());

        Assert.Empty(facets.Actions);
        Assert.Empty(facets.Actors);
        Assert.Equal(0, facets.TotalEntries);
        // Null et non l'époque Unix : « le journal est vide » n'est pas « le journal commence en 1970 ».
        Assert.Null(facets.EarliestEntry);
    }

    /// <summary>Le test qui compte : un journal d'audit est l'activité complète d'un locataire.</summary>
    [Fact]
    public async Task Another_organizations_log_is_neither_readable_nor_countable()
    {
        var mine = await FixtureAsync();
        var theirs = await FixtureAsync();

        await SeedAsync(theirs, "secret.created", actorUserId: theirs.Auth.User.Id, resourceType: "secret");
        await SeedAsync(theirs, "user.deleted", actorUserId: theirs.Auth.User.Id, resourceType: "user");

        // Mon propre journal ne voit rien du leur, ni en entrées ni en total.
        var page = await GetPageAsync(mine);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);

        // Les facettes non plus : elles trahiraient les actions et les acteurs sans montrer une
        // seule ligne.
        var facets = await GetFacetsAsync(mine);
        Assert.Empty(facets.Actions);
        Assert.Empty(facets.Actors);
        Assert.Equal(0, facets.TotalEntries);

        // Et viser explicitement leur organisation ne donne pas davantage : inexistant, pas
        // interdit — un 403 confirmerait que cette organisation existe.
        var orgId = theirs.Auth.User.OrgId;
        Assert.Equal(HttpStatusCode.NotFound,
            (await mine.Client.GetAsync($"/api/organizations/{orgId}/audit-log")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await mine.Client.GetAsync($"/api/organizations/{orgId}/audit-log/facets")).StatusCode);
    }

    [Fact]
    public async Task A_filter_cannot_reach_across_organizations()
    {
        var mine = await FixtureAsync();
        var theirs = await FixtureAsync();
        await SeedAsync(theirs, "secret.created", actorUserId: theirs.Auth.User.Id, resourceType: "secret", resourceId: "s-1");

        // Le périmètre vient du JWT : nommer l'acteur, l'action et la ressource exacts de l'autre
        // locataire ne doit rien rendre. C'est le prédicat d'organisation qui décide, pas les
        // filtres — et c'est ce que ce test vérifie plutôt que de le supposer.
        var page = await GetPageAsync(mine,
            $"?action=secret.created&actorUserId={theirs.Auth.User.Id}&resourceType=secret&resourceId=s-1");

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Take_is_clamped_rather_than_trusted()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "run.created");

        // Un `take` non borné est un déni de service trivial contre l'API comme contre Postgres.
        Assert.Equal(Paging.MaxTake, (await GetPageAsync(f, "?take=100000")).Take);
        Assert.Equal(1, (await GetPageAsync(f, "?take=0")).Take);
        Assert.Equal(0, (await GetPageAsync(f, "?skip=-10")).Skip);
    }

    [Fact]
    public async Task Only_a_maintainer_or_above_may_read_the_log()
    {
        var f = await FixtureAsync();
        await SeedAsync(f, "secret.created");
        var orgId = f.Auth.User.OrgId;

        foreach (var role in new[] { UserRole.Developer, UserRole.Viewer })
        {
            var (token, _) = await TestData.CreateUserWithRoleAsync(f.Client, role);
            using var client = TestData.AuthedClient(_factory, token);

            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/organizations/{orgId}/audit-log")).StatusCode);
            // Les facettes disent qui a fait quoi et combien de fois : elles sont du même niveau
            // de confidentialité que le journal lui-même, pas des métadonnées inoffensives.
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/organizations/{orgId}/audit-log/facets")).StatusCode);
        }

        var (maintainerToken, _) = await TestData.CreateUserWithRoleAsync(f.Client, UserRole.Maintainer);
        using var maintainer = TestData.AuthedClient(_factory, maintainerToken);
        Assert.Equal(HttpStatusCode.OK,
            (await maintainer.GetAsync($"/api/organizations/{orgId}/audit-log")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_get_nothing()
    {
        using var anonymous = _factory.CreateClient();
        var orgId = UlidGenerator.NewUlid();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/organizations/{orgId}/audit-log")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/organizations/{orgId}/audit-log/facets")).StatusCode);
    }

    // ---- helpers ----

    private sealed record Fixture(HttpClient Client, AuthResponse Auth);

    private async Task<Fixture> FixtureAsync()
    {
        var client = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(client);
        return new Fixture(TestData.AuthedClient(_factory, auth.Token), auth);
    }

    private Task<AuditPage> GetPageAsync(Fixture f, string query = "") =>
        GetAsync<AuditPage>(f.Client, $"/api/organizations/{f.Auth.User.OrgId}/audit-log{query}");

    private Task<AuditFacets> GetFacetsAsync(Fixture f) =>
        GetAsync<AuditFacets>(f.Client, $"/api/organizations/{f.Auth.User.OrgId}/audit-log/facets");

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(TestJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>
    /// Écrit une entrée directement par le dépôt : le but est de contrôler son horodatage, son
    /// acteur et sa ressource, ce qu'aucun chemin applicatif ne permet — <c>RecordAsync</c> fixe
    /// <c>created_at</c> à l'instant présent, et la table est append-only, donc rien ne pourra le
    /// corriger après coup.
    /// </summary>
    private async Task SeedAsync(
        Fixture f,
        string action,
        string? actorUserId = null,
        string? resourceType = null,
        string? resourceId = null,
        JsonNode? changes = null,
        JsonNode? details = null,
        int ageMinutes = 0,
        DateTime? at = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

        await repository.InsertAsync(new AuditLogEntry
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = f.Auth.User.OrgId,
            Action = action,
            ActorUserId = actorUserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Changes = changes,
            Details = details,
            CreatedAt = at ?? DateTime.UtcNow.AddMinutes(-ageMinutes),
        });
    }
}
