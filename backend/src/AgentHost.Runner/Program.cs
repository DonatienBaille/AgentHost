using AgentHost.Runner;
using AgentHost.Shared.Containers;
using Docker.DotNet;
using Serilog;
using Serilog.Formatting.Compact;

// ---------------------------------------------------------------------------------------------
// AgentHost.Runner — le tier d'exécution.
//
// Un processus par nœud (DaemonSet), qui détient le socket du démon de conteneurs local et
// n'expose que ce qu'il faut pour lancer, arrêter et lire un run. Il ne connaît ni la base, ni les
// organisations, ni les budgets : c'est ce qui permet au backend de redevenir réplicable, puisque
// « où tourne ce conteneur » cesse d'être une propriété implicite du nœud qui a reçu la requête.
//
// Voir docs/runner.md.
// ---------------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", "AgentHost.Runner")
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

builder.Host.UseSerilog();
builder.Services.AddSingleton(Log.Logger);

// Échec fermé : sans jeton partagé le processus ne démarre pas. Voir RunnerAuthentication.
var authToken = RunnerAuthentication.RequireToken(builder.Configuration);

// Le socket est résolu exactement comme le backend le résout (Docker:Host → DOCKER_HOST → sockets
// bien connus), pour qu'un opérateur n'ait pas deux grammaires de configuration selon le tier.
var runtimeEndpoint = ContainerRuntimeEndpoint.Resolve(builder.Configuration);
Log.Information(
    "Runner {RunnerId} using container runtime endpoint {Endpoint} ({Runtime}, {Source})",
    RunSupervisor.RunnerId, runtimeEndpoint.Uri, runtimeEndpoint.Runtime, runtimeEndpoint.Source);

builder.Services.AddSingleton(runtimeEndpoint);
builder.Services.AddSingleton<DockerClient>(_ =>
    new DockerClientConfiguration(new Uri(runtimeEndpoint.Uri)).CreateClient());

// Les chemins de montage sont ceux DU NŒUD. C'est le gain de fond du tier runner sur ce point :
// le workspace redevient local au processus qui parle au démon, donc Docker:HostWorkspacePath n'a
// plus à réconcilier la vue d'un pod backend avec celle d'un démon distant — sauf si le runner
// lui-même tourne en conteneur, cas où le réglage garde son sens.
var pathMapper = ContainerPathMapper.FromConfiguration(builder.Configuration);
if (pathMapper.RemapsPaths)
{
    Log.Information(
        "Run workspace {LocalRoot} is bind-mounted from {DaemonRoot} as the container daemon sees it",
        pathMapper.LocalWorkspaceRoot, pathMapper.DaemonWorkspaceRoot);
}
builder.Services.AddSingleton(pathMapper);

builder.Services.AddSingleton(ContainerLauncherOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ContainerLauncher>();
builder.Services.AddSingleton<RunSupervisor>();

builder.Services.AddHealthChecks()
    .AddCheck<ContainerRuntimeHealthCheck>("container-runtime", tags: new[] { "ready" });

var app = builder.Build();

// Adresse propre de ce pod, annoncée au backend à chaque lancement pour qu'il enregistre le NŒUD
// qui détient le run et non le Service par lequel il est passé. En Kubernetes :
// Runner__AdvertisedUrl: http://$(POD_IP):5001, POD_IP venant de l'API descendante.
var advertisedUrl = builder.Configuration["Runner:AdvertisedUrl"];
if (!string.IsNullOrWhiteSpace(advertisedUrl))
    Log.Information("Runner advertises itself to the backend as {AdvertisedUrl}", advertisedUrl);
else
    Log.Information("Runner has no Runner:AdvertisedUrl; the backend will keep the address it dialled. " +
                    "Correct only when a single runner answers that address.");

app.MapRunnerEndpoints(authToken, advertisedUrl);

// /health       — le processus répond. Aucune dépendance consultée, donc un démon momentanément
//                 indisponible ne fait pas redémarrer un pod runner qui tient encore des conteneurs
//                 vivants et dont la surveillance en mémoire serait perdue avec lui.
// /health/ready — le démon répond au ping : c'est la condition pour qu'on lui envoie des runs.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.Run();
