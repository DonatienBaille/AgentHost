using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Le chaînage de runs : un agent en déclenche un autre (feuille de route, lot 4).
///
/// <b>Ce que ces tests protègent avant tout, c'est une facture.</b> Un agent qui se chaîne lui-même
/// est une boucle infinie dont chaque maillon, pris isolément, est parfaitement légitime : rien
/// dans un run ne dit qu'il est le millième. Les trois limites — profondeur, éventail, taille
/// d'arbre — sont donc le garde-fou, pas un raffinement, et un test qui ne les exerce pas laisse
/// passer une dépense illimitée.
///
/// <b>Le second point est la forme de l'arbre.</b> `root_run_id` était posé à `parent`, ce qui est
/// juste à la profondeur 1 et faux ensuite : le petit-enfant aurait eu pour racine son parent, et
/// l'arbre se serait scindé en deux moitiés que rien ne relie — sans qu'aucune erreur n'apparaisse
/// nulle part.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RunChainingTests
{
    private readonly AgentHostApiFactory _factory;

    public RunChainingTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_agent_can_chain_another_run_from_its_own_run_token()
    {
        var f = await FixtureAsync();
        var parent = await CreateRunAsync(f);

        var child = await ChainAsync(parent, f.Agent.Id);

        var childRun = await LoadRunAsync(child.RunId);
        Assert.Equal(parent.Id, childRun.ParentRunId);
        Assert.Equal(parent.Id, childRun.RootRunId);
        Assert.Equal(1, childRun.ChainDepth);
        // Le type vient de l'endpoint, pas du corps : un agent ne déclare pas comment son enfant a
        // été déclenché.
        Assert.Equal(TriggeredByType.Chain, childRun.TriggeredByType);
        Assert.Equal(parent.ProjectId, childRun.ProjectId);
    }

    [Fact]
    public async Task A_grandchild_keeps_the_original_root_instead_of_starting_a_second_tree()
    {
        var f = await FixtureAsync();
        var root = await CreateRunAsync(f);

        var child = await ChainAsync(root, f.Agent.Id);
        var grandchild = await ChainAsync(await LoadRunAsync(child.RunId), f.Agent.Id);

        var grandchildRun = await LoadRunAsync(grandchild.RunId);
        Assert.Equal(child.RunId, grandchildRun.ParentRunId);
        // La racine est celle de l'arbre, pas le parent immédiat. C'est exactement le défaut que
        // portait la version précédente, et il ne se serait vu nulle part : les deux moitiés
        // d'arbre auraient l'air correctes séparément.
        Assert.Equal(root.Id, grandchildRun.RootRunId);
        Assert.Equal(2, grandchildRun.ChainDepth);
    }

    [Fact]
    public async Task The_chain_depth_is_capped()
    {
        var f = await FixtureAsync();
        var current = await CreateRunAsync(f);

        // On descend jusqu'à la limite : chaque maillon est légitime, c'est leur accumulation qui
        // ne l'est pas.
        for (var depth = 1; depth <= RunService.MaxChainDepth; depth++)
        {
            var next = await ChainAsync(current, f.Agent.Id);
            current = await LoadRunAsync(next.RunId);
            Assert.Equal(depth, current.ChainDepth);
        }

        var refused = await ChainRawAsync(current, f.Agent.Id);
        // 422 et non 500 : la requête est comprise, c'est son effet qui est refusé. L'agent doit
        // pouvoir traiter ce refus plutôt que croire à une panne et réessayer.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("depth", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_fan_out_of_a_single_run_is_capped()
    {
        var f = await FixtureAsync();
        var parent = await CreateRunAsync(f);

        for (var i = 0; i < RunService.MaxChainChildren; i++)
            await ChainAsync(parent, f.Agent.Id);

        var refused = await ChainRawAsync(parent, f.Agent.Id);

        // La profondeur seule n'attraperait pas ce cas : l'arbre reste plat, et il explose quand
        // même.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(RunService.MaxChainChildren, await CountChildrenAsync(parent.Id));
    }

    [Fact]
    public async Task A_run_cannot_chain_an_agent_of_another_project()
    {
        var f = await FixtureAsync();
        var otherProject = await TestData.CreateProjectAsync(f.Client, TestData.Suffix());
        var otherAgent = await TestData.CreateAgentAsync(f.Client, otherProject.Id, TestData.Suffix());
        var parent = await CreateRunAsync(f);

        var refused = await ChainRawAsync(parent, otherAgent.Id);

        // Même organisation, autre projet : sans cette barrière, un agent enfermé dans un projet
        // ferait dépenser le budget d'un autre, et l'isolation s'arrêterait à la porte du premier
        // run.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task A_run_token_only_chains_from_its_own_run()
    {
        var f = await FixtureAsync();
        var mine = await CreateRunAsync(f);
        var other = await CreateRunAsync(f);

        // Jeton de `mine`, route de `other` : le protocole vérifie l'égalité à chaque appel, et
        // celui-ci ne fait pas exception — c'est le seul qui crée une dépense.
        using var client = AgentClient(mine);
        var response = await client.PostJsonAsync($"/api/agent/runs/{other.Id}/chain",
            new AgentChainRequest { AgentId = f.Agent.Id });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountChildrenAsync(other.Id));
    }

    [Fact]
    public async Task A_user_token_cannot_speak_the_chain_endpoint()
    {
        var f = await FixtureAsync();
        var parent = await CreateRunAsync(f);

        // Le jeton utilisateur a une autre audience : l'accepter ici reviendrait à confondre les
        // deux surfaces d'authentification du dépôt.
        var response = await f.Client.PostJsonAsync($"/api/agent/runs/{parent.Id}/chain",
            new AgentChainRequest { AgentId = f.Agent.Id });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chaining_without_an_agent_is_a_bad_request_not_a_crash()
    {
        var f = await FixtureAsync();
        var parent = await CreateRunAsync(f);

        using var client = AgentClient(parent);
        var response = await client.PostJsonAsync($"/api/agent/runs/{parent.Id}/chain",
            new AgentChainRequest { AgentId = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- lecture de l'arbre ----

    [Fact]
    public async Task The_tree_is_readable_from_any_of_its_members()
    {
        var f = await FixtureAsync();
        var root = await CreateRunAsync(f);
        var child = await LoadRunAsync((await ChainAsync(root, f.Agent.Id)).RunId);
        var grandchild = await LoadRunAsync((await ChainAsync(child, f.Agent.Id)).RunId);

        // Depuis la feuille, et pas seulement depuis la racine : on arrive sur un run parce qu'il a
        // échoué ou coûté cher, et c'est à ce moment-là qu'on veut savoir de quelle cascade il fait
        // partie. Exiger la racine obligerait à la connaître déjà.
        var fromLeaf = await GetAsync<RunTreeResponse>(f.Client, $"/api/runs/{grandchild.Id}/tree");
        var fromRoot = await GetAsync<RunTreeResponse>(f.Client, $"/api/runs/{root.Id}/tree");

        Assert.Equal(root.Id, fromLeaf.RootRunId);
        Assert.Equal(fromRoot.RootRunId, fromLeaf.RootRunId);
        Assert.Equal(3, fromLeaf.TotalRuns);
        Assert.Equal(2, fromLeaf.MaxDepth);

        // Et l'arbre est bien un arbre, pas une liste à plat.
        var rootNode = Assert.IsType<RunTreeNode>(fromLeaf.Root);
        var childNode = Assert.Single(rootNode.Children);
        Assert.Equal(child.Id, childNode.Id);
        Assert.Equal(grandchild.Id, Assert.Single(childNode.Children).Id);
        // Le nom de l'agent est résolu côté serveur : un identifiant seul n'apprend rien à qui lit
        // l'arbre.
        Assert.Equal(f.Agent.Name, rootNode.AgentName);
    }

    [Fact]
    public async Task A_run_with_no_chain_is_a_tree_of_one()
    {
        var f = await FixtureAsync();
        var run = await CreateRunAsync(f);

        var tree = await GetAsync<RunTreeResponse>(f.Client, $"/api/runs/{run.Id}/tree");

        // Pas de cas particulier à écrire côté IHM : un run isolé est simplement une racine sans
        // enfant.
        Assert.Equal(run.Id, tree.RootRunId);
        Assert.Equal(1, tree.TotalRuns);
        Assert.Equal(0, tree.MaxDepth);
        Assert.Empty(tree.Root!.Children);
    }

    [Fact]
    public async Task Another_organizations_tree_is_not_readable()
    {
        var mine = await FixtureAsync();
        var theirs = await FixtureAsync();
        var theirRun = await CreateRunAsync(theirs);
        await ChainAsync(theirRun, theirs.Agent.Id);

        // Inexistant, jamais interdit — comme partout ailleurs dans le dépôt.
        Assert.Equal(HttpStatusCode.NotFound,
            (await mine.Client.GetAsync($"/api/runs/{theirRun.Id}/tree")).StatusCode);
    }

    // ---- helpers ----

    private sealed record Fixture(HttpClient Client, AuthResponse Auth, Project Project, Agent Agent);

    private async Task<Fixture> FixtureAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        return new Fixture(client, auth, project, agent);
    }

    private async Task<Run> CreateRunAsync(Fixture f)
    {
        var response = await f.Client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = f.Agent.Id });
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        return run!;
    }

    /// <summary>Un client authentifié exactement comme le conteneur d'agent : avec AGENTHOST_RUN_TOKEN.</summary>
    private HttpClient AgentClient(Run run)
    {
        var token = _factory.Services.GetRequiredService<IRunTokenService>()
            .Issue(run.Id, run.ProjectId, run.OrgId, 3600);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpResponseMessage> ChainRawAsync(Run parent, string agentId)
    {
        using var client = AgentClient(parent);
        return await client.PostJsonAsync($"/api/agent/runs/{parent.Id}/chain",
            new AgentChainRequest { AgentId = agentId });
    }

    private async Task<AgentChainResponse> ChainAsync(Run parent, string agentId)
    {
        var response = await ChainRawAsync(parent, agentId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentChainResponse>(TestJson.Options))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(TestJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    private async Task<Run> LoadRunAsync(string runId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRunRepository>().GetAsync(runId)
            ?? throw new InvalidOperationException($"Run {runId} not found");
    }

    private async Task<int> CountChildrenAsync(string parentRunId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRunRepository>().CountChildrenAsync(parentRunId);
    }
}
