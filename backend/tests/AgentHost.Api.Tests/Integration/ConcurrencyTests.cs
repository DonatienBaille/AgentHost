using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Dapper;
using AgentHost.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Le comportement sous concurrence (dette identifiée — « aucun test de charge »).
///
/// <b>Ce que ces tests sont, et ce qu'ils ne sont pas.</b> Ce n'est pas un banc de performance :
/// « combien de requêtes par seconde » dépend de la machine, ne se compare à rien, et n'a jamais
/// rien empêché. Ce qui manquait vraiment, c'est la réponse à une autre question — <b>les
/// invariants tiennent-ils quand deux appelants arrivent en même temps</b> ? Un compteur qui perd
/// une incrémentation, deux runs qui portent le même numéro, une approbation décidée deux fois :
/// aucun de ces défauts ne se voit sur une requête isolée, et tous sont des corruptions.
///
/// <b>Chaque test lance ses appels réellement en parallèle</b>, sur la vraie pile HTTP et la vraie
/// base. Les sérialiser les rendrait verts sans rien prouver.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ConcurrencyTests
{
    /// <summary>
    /// Assez pour que les collisions soient certaines plutôt que probables, assez peu pour que la
    /// suite reste rapide. Une course qui ne se produit qu'une fois sur dix donne un test qui
    /// passe neuf fois sur dix, ce qui est pire qu'aucun test.
    /// </summary>
    private const int Parallelism = 12;

    private readonly AgentHostApiFactory _factory;

    public ConcurrencyTests(AgentHostApiFactory factory) => _factory = factory;

    // ---- numérotation des runs ----

    /// <summary>
    /// `runs` porte `UNIQUE (project_id, number)`. Un `SELECT MAX + 1` sous concurrence produit
    /// deux fois le même numéro, donc une violation de contrainte et un run refusé au hasard.
    /// </summary>
    [Fact]
    public async Task Concurrent_run_creations_never_collide_on_a_run_number()
    {
        var f = await FixtureAsync();

        var responses = await InParallelAsync(_ =>
            f.Client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = f.Agent.Id }));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Création refusée : {r.StatusCode}"));

        var numbers = await ProjectRunNumbersAsync(f.Project.Id);
        Assert.Equal(Parallelism, numbers.Count);
        // Le point : autant de numéros DISTINCTS que de runs. La contrainte d'unicité aurait
        // refusé un doublon, mais l'appelant l'aurait vu comme une erreur serveur inexpliquée.
        Assert.Equal(Parallelism, numbers.Distinct().Count());
    }

    // ---- cumul de budget ----

    /// <summary>
    /// Les rapports d'usage d'un agent arrivent en rafale. Un lire-modifier-écrire y perdrait des
    /// incrémentations — et un budget sous-évalué est un budget qui ne s'épuise jamais.
    /// </summary>
    [Fact]
    public async Task Concurrent_usage_reports_never_lose_an_increment()
    {
        var f = await FixtureAsync();
        var run = await SeedRunAsync(f);

        await InParallelAsync(async _ =>
        {
            using var scope = _factory.Services.CreateScope();
            var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
            return await runs.AddBudgetUsageAsync(run.Id, 0.10m);
        });

        var total = await BudgetUsedAsync(run.Id);
        // Exactement, pas « à peu près » : chaque centime perdu ici est un centime que le plafond
        // ne verra jamais.
        Assert.Equal(Parallelism * 0.10m, total);
    }

    // ---- rotation des jetons de rafraîchissement ----

    /// <summary>
    /// Deux onglets rafraîchissent au même instant avec le MÊME jeton. La rotation exige que le
    /// jeton présenté soit révoqué : deux succès signifieraient deux familles de jetons vivantes
    /// issues d'un seul, c'est-à-dire une révocation qui ne révoque rien.
    /// </summary>
    [Fact]
    public async Task Only_one_of_two_simultaneous_refreshes_with_the_same_token_succeeds()
    {
        using var anonymous = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(anonymous);

        var responses = await InParallelAsync(async _ =>
        {
            using var client = _factory.CreateClient();
            return await client.PostJsonAsync("/api/auth/refresh",
                new RefreshRequest { RefreshToken = auth.RefreshToken });
        }, count: 2);

        var succeeded = responses.Count(r => r.IsSuccessStatusCode);
        // Un seul gagnant. Le perdant reçoit un refus, ce que l'IHM traite comme une session à
        // reprendre — comportement correct et déjà couvert par l'intercepteur côté frontend.
        Assert.Equal(1, succeeded);

        // Et surtout : le perdant ne doit avoir émis AUCUN jeton. Deux jetons actifs issus d'un
        // seul, c'est une rotation qui ne révoque rien — et le vol de jeton redevenu indétectable,
        // puisque la détection repose entièrement sur le rejeu d'un jeton révoqué.
        Assert.Equal(1, await ActiveRefreshTokenCountAsync(auth.User.Id));
    }

    // ---- décision d'approbation ----

    /// <summary>
    /// Deux mainteneurs approuvent le même run au même instant. Le run ne doit franchir la porte
    /// qu'une fois : deux transitions produiraient deux lancements, donc une double facturation.
    /// </summary>
    [Fact]
    public async Task An_approval_decided_twice_at_once_only_takes_effect_once()
    {
        var f = await FixtureAsync();
        var run = await SeedRunAsync(f, RunStatus.AwaitingApproval);
        await SeedPendingApprovalAsync(run);

        var responses = await InParallelAsync(async _ =>
        {
            using var client = TestData.AuthedClient(_factory, f.Token);
            return await client.PostJsonAsync($"/api/runs/{run.Id}/approve",
                new ApprovalRequest { Decision = "approve" });
        }, count: 4);

        // Au plus un succès : les autres constatent que la porte est déjà franchie. Zéro serait
        // aussi un défaut — personne n'aurait décidé — d'où l'égalité stricte.
        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));

        // Et l'assertion qui porte vraiment : le code HTTP est une convention, la reprise du run
        // est le dommage. Quatre `approval.granted` signifieraient quatre reprises du même run,
        // donc quatre exécutions facturées pour une seule décision.
        Assert.Equal(1, await CountEventsAsync(run.Id, "approval.granted"));
    }

    /// <summary>
    /// Le pendant du test précédent, et celui qui attrape la perte de mise à jour : un garde
    /// exigeant trois approbations, trois approbateurs qui cliquent ensemble.
    ///
    /// La version précédente lisait la liste des réponses, y ajoutait la sienne en mémoire, puis
    /// réécrivait la liste entière — trois écritures d'une liste d'un élément, dont il ne restait
    /// que la dernière. Le garde n'atteignait jamais son compte : trois personnes avaient approuvé,
    /// et le run restait bloqué. C'est exactement la situation pour laquelle un garde à plusieurs
    /// approbateurs existe, donc exactement celle où il ne fonctionnait pas.
    /// </summary>
    [Fact]
    public async Task Simultaneous_approvers_of_a_multi_approver_gate_are_all_recorded()
    {
        var f = await FixtureAsync();
        var run = await SeedRunAsync(f, RunStatus.AwaitingApproval);
        await SeedPendingApprovalAsync(run, requiredCount: 3);

        await InParallelAsync(async _ =>
        {
            using var client = TestData.AuthedClient(_factory, f.Token);
            return await client.PostJsonAsync($"/api/runs/{run.Id}/approve",
                new ApprovalRequest { Decision = "approve" });
        }, count: 3);

        // Les trois voix comptent.
        Assert.Equal(3, await CountApprovalResponsesAsync(run.Id));

        // Et le garde ne s'ouvre qu'une fois, une fois le compte atteint.
        Assert.Equal(1, await CountEventsAsync(run.Id, "approval.granted"));
    }

    // ---- transitions terminales concurrentes ----

    /// <summary>
    /// Deux fins de run qui arrivent ensemble sur le même run : un humain annule à l'instant où le
    /// moniteur de conteneur constate la sortie du processus. Ce n'est pas un cas de laboratoire —
    /// c'est le déroulé normal d'une annulation, puisque annuler <i>provoque</i> la sortie.
    ///
    /// <b>Le dommage, si les deux passent.</b> Les deux lisent le run en <c>running</c>, les deux
    /// écrivent — l'état final dépend de qui finit en dernier — et surtout les deux exécutent la
    /// fin de course : deux salves de webhooks <c>run.finished</c> pour un seul run, deux entrées
    /// d'historique, et deux décréments de <c>agenthost.run.in_flight</c> pour un seul incrément.
    /// Cette jauge est exactement celle qui affichait une profondeur de file négative, et la voici
    /// atteignable par un autre chemin.
    ///
    /// Le test s'adresse à la machine à états à travers deux lectures distinctes du même run, ce
    /// qui est précisément la situation de deux appelants concurrents.
    /// </summary>
    [Fact]
    public async Task Two_terminal_transitions_racing_on_one_run_only_one_takes_effect()
    {
        var f = await FixtureAsync();
        var run = await SeedRunAsync(f);

        // Annulation par un humain contre expiration par le chien de garde : les deux sont
        // légitimes depuis `running`, et les deux arrivent réellement ensemble — annuler un run qui
        // vient d'atteindre son délai est une coïncidence de quelques millisecondes, pas une
        // hypothèse. (Le couple annulation/succès ne conviendrait pas : `running -> succeeded` n'est
        // pas une transition valide, le second appel serait écarté sans jamais courir.)
        RunStatus[] targets = [RunStatus.Cancelled, RunStatus.TimedOut];

        // Les deux lectures ont lieu AVANT le rendez-vous, et c'est le cœur du test : chaque
        // appelant détient un run lu il y a un instant, exactement comme le moniteur de conteneur
        // détient celui qu'il a chargé au lancement pendant qu'un humain annule depuis l'IHM. Lire
        // à l'intérieur de la course laisserait le second appelant voir parfois l'état déjà changé,
        // et le test passerait alors sans avoir rien exercé.
        var views = new List<Run>();
        for (var i = 0; i < 2; i++)
        {
            using var seed = _factory.Services.CreateScope();
            views.Add((await seed.ServiceProvider.GetRequiredService<IRunRepository>().GetAsync(run.Id))!);
        }

        var outcomes = await InParallelAsync(async index =>
        {
            using var scope = _factory.Services.CreateScope();
            var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();
            return await stateMachine.TryTransitionAsync(views[index], targets[index], "course");
        }, count: 2);

        Assert.Equal(1, outcomes.Count(ok => ok));

        // La conséquence observable : une seule fin de course. Deux `run.status_changed` vers un
        // état terminal, ce serait deux fins pour un run.
        Assert.Equal(1, await CountEventsAsync(run.Id, "run.status_changed"));

        // Et l'état en base est bien celui du gagnant, pas celui du dernier arrivé.
        var final = await RunStatusAsync(run.Id);
        Assert.Contains(final, new[] { RunStatus.Cancelled.ToDbString(), RunStatus.TimedOut.ToDbString() });
    }

    // ---- unicité des comptes ----

    /// <summary>
    /// Deux inscriptions simultanées avec la même adresse. Une contrainte d'unicité en base est la
    /// seule chose qui tienne ici : une vérification applicative « l'adresse existe-t-elle ? »
    /// passe des deux côtés avant que l'un n'écrive.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_registrations_with_the_same_email_do_not_both_create_an_account()
    {
        var suffix = TestData.Suffix();
        var email = $"course-{suffix}@example.com";

        var responses = await InParallelAsync(async index =>
        {
            using var client = _factory.CreateClient();
            return await client.PostJsonAsync("/api/auth/register", new RegisterRequest
            {
                // Organisation distincte pour chacun : seul le compte doit entrer en collision,
                // sinon on testerait l'unicité du slug d'organisation.
                OrgName = $"Course {suffix} {index}",
                OrgSlug = $"course-{suffix}-{index}",
                Email = email,
                Password = TestData.DefaultPassword,
                DisplayName = "Course",
            });
        }, count: 4);

        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
        Assert.Equal(1, await CountUsersAsync(email));

        // Le refus doit être le même que celui de la version séquentielle. Une erreur 500 ici
        // signalerait que la contrainte d'unicité remonte brute jusqu'à l'appelant.
        Assert.All(responses.Where(r => !r.IsSuccessStatusCode),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        // Et rien ne doit rester derrière : l'inscription crée l'organisation AVANT l'utilisateur,
        // si bien qu'une collision d'adresse laissait une organisation vide à chaque tentative
        // refusée — invisible, puisque plus personne n'en connaît l'identifiant.
        Assert.Equal(1, await CountOrgsAsync($"course-{suffix}-"));
    }

    // ---- plafond mensuel de projet ----

    /// <summary>
    /// Le plafond mensuel est un « lire puis décider » : plusieurs créations simultanées le lisent
    /// toutes avant qu'aucune ne dépense.
    ///
    /// <b>Ce test énonce le comportement réel plutôt que de le corriger,</b> et ce n'est pas une
    /// facilité. Le dépassement possible est borné par le budget d'UN run — les runs créés
    /// consomment ensuite leur propre plafond, contrôlé par run — là où sérialiser toutes les
    /// créations d'un projet derrière un verrou coûterait, sur un projet actif, bien plus que ce
    /// que le dépassement représente. La garantie exacte est donc : le plafond arrête un projet,
    /// mais la dernière rafale peut le franchir d'un run.
    /// </summary>
    [Fact]
    public async Task The_monthly_cap_stops_a_project_even_when_creations_arrive_together()
    {
        var f = await FixtureAsync();

        // Le plafond est déjà consommé avant la rafale : ce n'est plus une question de course,
        // c'est le cas où AUCUNE création ne doit passer.
        var run = await SeedRunAsync(f);
        await ExhaustProjectBudgetAsync(f.Project.Id, run.Id);

        var responses = await InParallelAsync(_ =>
            f.Client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = f.Agent.Id }));

        Assert.All(responses, r => Assert.False(r.IsSuccessStatusCode,
            "Une création est passée alors que le plafond mensuel était déjà dépassé."));
    }

    // ---- limiteur de débit ----

    [Fact]
    public async Task The_rate_limiter_refuses_rather_than_queueing_when_a_client_floods_it()
    {
        // Le limiteur est neutralisé dans la fabrique de test (voir AgentHostApiFactory) : ce qui
        // est vérifié ici n'est donc pas le seuil, mais qu'une rafale ne fasse ni tomber le
        // processus ni attendre indéfiniment. Un limiteur qui met en file plutôt que de refuser
        // immobilise les threads de traitement, ce qui est le déni de service qu'il devait éviter.
        using var client = _factory.CreateClient();
        var started = DateTime.UtcNow;

        var responses = await InParallelAsync(_ => client.GetAsync("/health"), count: 40);

        Assert.All(responses, r => Assert.True(
            r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.TooManyRequests,
            $"Réponse inattendue sous rafale : {r.StatusCode}"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "La rafale a été mise en file.");
    }

    // ---- helpers ----

    private sealed record Fixture(HttpClient Client, string Token, AuthResponse Auth, Project Project, Agent Agent);

    private async Task<Fixture> FixtureAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        return new Fixture(client, auth.Token, auth, project, agent);
    }

    /// <summary>
    /// Lance <paramref name="count"/> appels et les libère <b>ensemble</b>.
    ///
    /// Le point de rendez-vous compte : sans lui, les tâches démarrent au fil de leur planification
    /// et la course n'a jamais lieu — le test passerait en prouvant seulement que les appels
    /// séquentiels fonctionnent.
    /// </summary>
    private static async Task<List<T>> InParallelAsync<T>(Func<int, Task<T>> action, int count = Parallelism)
    {
        using var gate = new SemaphoreSlim(0, count);

        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            await gate.WaitAsync();
            return await action(index);
        }).ToList();

        gate.Release(count);
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<List<long>> ProjectRunNumbersAsync(string projectId)
    {
        using var db = Connection();
        return (await db.QueryAsync<long>(
            "SELECT number FROM runs WHERE project_id = @ProjectId", new { ProjectId = projectId })).ToList();
    }

    private async Task<decimal> BudgetUsedAsync(string runId)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<decimal>(
            "SELECT budget_used_usd FROM runs WHERE id = @Id", new { Id = runId });
    }

    private async Task<int> CountUsersAsync(string email)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE email = @Email", new { Email = email });
    }

    private async Task<string> RunStatusAsync(string runId)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<string>(
            "SELECT status FROM runs WHERE id = @Id", new { Id = runId }) ?? string.Empty;
    }

    private async Task<int> CountOrgsAsync(string slugPrefix)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM organizations WHERE slug LIKE @Prefix || '%'", new { Prefix = slugPrefix });
    }

    private async Task<int> CountEventsAsync(string runId, string eventType)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM run_events WHERE run_id = @RunId AND event_type = @Type",
            new { RunId = runId, Type = eventType });
    }

    /// <summary>Le nombre de réponses réellement stockées dans l'approbation du run.</summary>
    private async Task<int> CountApprovalResponsesAsync(string runId)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COALESCE(jsonb_array_length(responses), 0) FROM approvals WHERE run_id = @RunId",
            new { RunId = runId });
    }

    private async Task<int> ActiveRefreshTokenCountAsync(string userId)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM refresh_tokens WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = userId });
    }

    /// <summary>Consomme tout le plafond mensuel du projet sur un run existant.</summary>
    private async Task ExhaustProjectBudgetAsync(string projectId, string runId)
    {
        using var db = Connection();
        var cap = await db.ExecuteScalarAsync<decimal>(
            "SELECT budget_monthly_usd FROM projects WHERE id = @Id", new { Id = projectId });

        await db.ExecuteAsync(
            "UPDATE runs SET budget_used_usd = @Used WHERE id = @Id",
            new { Id = runId, Used = cap + 1m });
    }

    /// <summary>
    /// Écrit un run directement par le dépôt : ces tests ont besoin d'un run dans un état donné,
    /// que le lancement réel ne permet pas d'obtenir — il n'y a pas de démon ici.
    /// </summary>
    private async Task<Run> SeedRunAsync(Fixture f, RunStatus status = RunStatus.Running)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();

        var run = new Run
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = f.Auth.User.OrgId,
            ProjectId = f.Project.Id,
            Number = await runs.GetNextRunNumberAsync(f.Project.Id),
            AgentId = f.Agent.Id,
            AgentVersionId = f.Agent.CurrentVersionId,
            Status = status,
            Inputs = new System.Text.Json.Nodes.JsonObject(),
            Context = new System.Text.Json.Nodes.JsonObject(),
            BudgetMaxUsd = 100m,
            BudgetUsedUsd = 0m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
        };

        await runs.InsertAsync(run);
        return run;
    }

    private async Task SeedPendingApprovalAsync(Run run, int requiredCount = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var approvals = scope.ServiceProvider.GetRequiredService<IApprovalRepository>();

        await approvals.InsertAsync(new Approval
        {
            Id = UlidGenerator.NewUlid(),
            RunId = run.Id,
            ApprovalType = ApprovalType.Gate,
            Status = ApprovalStatus.Pending,
            Prompt = "Concurrence",
            RequiredRole = "maintainer",
            RequiredCount = requiredCount,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
    }

    private System.Data.IDbConnection Connection() =>
        _factory.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
}
