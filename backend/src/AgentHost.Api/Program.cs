using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgentHost.Api.Endpoints;
using AgentHost.Api.Hubs;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Infrastructure.Storage;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Services.Email;
using AgentHost.Shared.Containers;
using Docker.DotNet;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR; // HubOptionsExtensions.AddFilter
using Microsoft.IdentityModel.Tokens;
using Npgsql; // TracerProviderBuilder.AddNpgsql (Npgsql.OpenTelemetry)
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ---- Serilog structured logging (spec section 14.1) ----
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ApplicationName", "AgentHost.Backend")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        Path.Combine(builder.Environment.ContentRootPath, "logs", "agenthost-.json"),
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();
builder.Services.AddSingleton(Log.Logger);

// Configures Dapper's snake_case <-> PascalCase column mapping and jsonb/enum type handlers.
DapperBootstrap.Configure();

// ---- Infrastructure ----
// ICallerContext exposes the *authenticated* caller's org/user/role. Authorization decisions
// must be based on it, never on an orgId/userId taken from the request itself.
builder.Services.AddHttpContextAccessor();
// One instance per scope serving both roles: the hub filter binds the principal through
// ICallerContextBinder and consumers read it back through ICallerContext.
builder.Services.AddScoped<CallerContext>();
builder.Services.AddScoped<ICallerContext>(sp => sp.GetRequiredService<CallerContext>());
builder.Services.AddScoped<ICallerContextBinder>(sp => sp.GetRequiredService<CallerContext>());

builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton(sp => new MigrationRunner(
    builder.Configuration, sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<Serilog.ILogger>()));

// Container runtime endpoint. Docker and Podman both speak the Docker API, so one client drives
// either; only the socket location differs, and it is discovered rather than declared. See
// Infrastructure/ContainerRuntimeEndpoint.cs.
var containerRuntime = ContainerRuntimeEndpoint.Resolve(builder.Configuration);
Log.Information(
    "Container runtime endpoint {Endpoint} ({Runtime}, {Source})",
    containerRuntime.Uri, containerRuntime.Runtime, containerRuntime.Source);
builder.Services.AddSingleton(containerRuntime);
builder.Services.AddSingleton<DockerClient>(_ =>
    new DockerClientConfiguration(new Uri(containerRuntime.Uri)).CreateClient());

// Maps the backend's own view of the run workspace onto the view the container daemon has, so
// bind mounts work when the backend is itself containerized (Docker-in-Docker, rootless Podman).
var containerPathMapper = ContainerPathMapper.FromConfiguration(builder.Configuration);
if (containerPathMapper.RemapsPaths)
{
    Log.Information(
        "Run workspace {LocalRoot} is bind-mounted from {DaemonRoot} as the container daemon sees it",
        containerPathMapper.LocalWorkspaceRoot, containerPathMapper.DaemonWorkspaceRoot);
}
builder.Services.AddSingleton(containerPathMapper);

// La plomberie conteneur elle-même (AgentHost.Shared) : le même objet sert au mode inprocess ici
// et au tier runner dans AgentHost.Runner. Sans état propre, donc singleton.
builder.Services.AddSingleton(ContainerLauncherOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<ContainerLauncher>();

// ---- Artifact storage: local disk (default) or any S3-compatible object store ----
// Resolved eagerly so a broken S3 configuration fails at startup with the full list of problems,
// rather than on the first upload with one symptom.
var artifactStorage = CreateArtifactStorage(builder.Configuration, Log.Logger);
builder.Services.AddSingleton<IArtifactStorage>(artifactStorage);

// Redis is optional (spec 4.1: "optional, cache/sessions") — wrapped in a holder so a
// missing/unreachable Redis never blocks startup; consumers check RedisConnectionHolder.Multiplexer
// for null before use.
//
// Connected eagerly here (rather than lazily inside the DI factory) because the SignalR
// registration below has to know, at service-registration time, whether a backplane can be wired
// up. `redisConnection` is null whenever Redis is unconfigured or unreachable.
var redisConnection = ConnectRedis(builder.Configuration, Log.Logger);
builder.Services.AddSingleton(new RedisConnectionHolder(redisConnection));

// Cache d'agrégats (lot 4, phase P3). Redis n'était jusqu'ici que le backplane SignalR ; c'est
// l'écart que la feuille de route appelait « cache Redis réellement utilisé ». Sans Redis, le
// comportement reste EXACTEMENT celui d'avant : NoAggregateCache passe tout au calcul direct.
builder.Services.AddSingleton<IAggregateCache>(sp => redisConnection is not null
    ? new RedisAggregateCache(redisConnection, sp.GetRequiredService<Serilog.ILogger>())
    : new NoAggregateCache());

// ---- Repositories ----
builder.Services.AddScoped<IRunRepository, RunRepository>();
builder.Services.AddScoped<IRunEventRepository, RunEventRepository>();
builder.Services.AddScoped<IApprovalRepository, ApprovalRepository>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();
builder.Services.AddScoped<IAgentVersionRepository, AgentVersionRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IOrganizationRepository, OrganizationRepository>();
builder.Services.AddScoped<IMemoryRepository, MemoryRepository>();
builder.Services.AddScoped<IArtifactRepository, ArtifactRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<ISecretRepository, SecretRepository>();
builder.Services.AddScoped<IWebhookRepository, WebhookRepository>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IInvitationRepository, InvitationRepository>();
builder.Services.AddScoped<IPasswordResetTokenRepository, PasswordResetTokenRepository>();
builder.Services.AddScoped<IUserMfaRepository, UserMfaRepository>();
builder.Services.AddScoped<ITriggerRepository, TriggerRepository>();

// ---- Services ----
builder.Services.AddScoped<IRunService, RunService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<ITriggerService, TriggerService>();
builder.Services.AddScoped<IOrgPurgeService, OrgPurgeService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
// ---- Orchestration : en processus (défaut) ou déléguée au tier runner ----
//
// « inprocess » est le défaut et le reste : Runner:Mode absent, vide ou inconnu vaut inprocess, de
// sorte qu'une installation existante qui met à jour son binaire ne change strictement pas de
// comportement. Seul le mot exact « remote » bascule, et il exige alors Runner:BaseUrl et
// Runner:AuthToken — RunnerOptions lève au démarrage plutôt que de dégrader en silence, ce qui
// donnerait un déploiement multi-répliques dont le contrôle des runs serait cassé sans le dire.
//
// Voir docs/runner.md et Services/RemoteContainerOrchestrator.cs.
var runnerOptions = RunnerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(runnerOptions);

if (runnerOptions.IsRemote)
{
    Log.Information("Container orchestration delegated to the runner tier at {RunnerBaseUrl}", runnerOptions.BaseUrl);
    builder.Services.AddScoped<IContainerOrchestrator, RemoteContainerOrchestrator>();
    builder.Services.AddHttpClient(RemoteContainerOrchestrator.HttpClientName, client =>
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", runnerOptions.AuthToken);
    });
}
else
{
    builder.Services.AddScoped<IContainerOrchestrator, ContainerOrchestrator>();
}

// Conséquences DB/état de la sortie d'un conteneur, partagées par les deux modes d'orchestration.
builder.Services.AddSingleton<RunCompletionRecorder>();
builder.Services.AddScoped<IEventBus, SignalREventBus>();
builder.Services.AddScoped<RunStateMachine>();
builder.Services.AddSingleton<AgentHostMetrics>();
builder.Services.AddScoped<ISecretsBroker, SecretsBroker>();
builder.Services.AddScoped<ISecretsRekeyService, SecretsRekeyService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAuthTokenIssuer, AuthTokenIssuer>();
builder.Services.AddScoped<IInvitationService, InvitationService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<IMfaService, MfaService>();
builder.Services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
builder.Services.AddSingleton<IAgentManifestParser, AgentManifestParser>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
// Issues the MFA login-challenge token. Deliberately a different audience from the access token,
// so the bearer scheme configured below rejects it; see Infrastructure/MfaChallengeTokenService.cs.
builder.Services.AddSingleton<IMfaChallengeTokenService, MfaChallengeTokenService>();
builder.Services.AddHttpClient();

// Optional breached-password screening (Have I Been Pwned k-anonymity range API). Off unless
// Auth:BreachedPasswordCheck:Enabled is true, and fail-open in every error case, so an unreachable
// third party can never block a signup - see Services/BreachedPasswordChecker.cs.
builder.Services.AddHttpClient(BreachedPasswordChecker.HttpClientName, client =>
{
    // HIBP requires a descriptive user agent and rejects requests without one.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentHost-Backend");
});
builder.Services.AddSingleton<IBreachedPasswordChecker, BreachedPasswordChecker>();

// ---- Courriel sortant ----
// Aucun envoi tant que Email:Provider n'a pas été mis à « smtp » : le défaut est NoOpEmailSender,
// qui journalise en Warning ce qui ne part pas. Les appelants ne parlent jamais à l'expéditeur
// directement mais à IEmailDispatcher, qui met en file et rend la main immédiatement — ce qui
// garde l'acheminement (sa latence comme ses échecs) hors du chemin de réponse HTTP, propriété
// dont dépend l'absence d'oracle d'énumération sur POST /api/auth/password-reset/request.
// Voir Services/Email/EmailDispatcher.cs.
var emailOptions = EmailOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton(sp => EmailSenderFactory.Create(emailOptions, sp.GetRequiredService<ILogger>()));
builder.Services.AddSingleton<IEmailOutboxRepository, EmailOutboxRepository>();
builder.Services.AddSingleton<BackgroundEmailDispatcher>();
builder.Services.AddSingleton<IEmailDispatcher>(sp => sp.GetRequiredService<BackgroundEmailDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundEmailDispatcher>());

// ---- Validation ----
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// ---- AuthN/AuthZ (JWT bearer; roles: owner > maintainer > developer > viewer) ----
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret is not configured");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "agenthost";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "agenthost";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Without this, the handler remaps short claim names (e.g. "sub") to long legacy URIs
        // (ClaimTypes.NameIdentifier) via its DefaultInboundClaimTypeMap, which would break any
        // code — like GET /api/auth/me — that reads JwtRegisteredClaimNames.Sub off the principal.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // Browsers can't set Authorization headers on WebSocket upgrades, so SignalR clients
        // pass the token as ?access_token=... instead; forward it into the normal JWT pipeline.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorizationPolicies();

// ---- Agent callback protocol (docs/agent-protocol.md) ----
// Adds the run-scoped "AgentRun" bearer scheme (distinct audience), its authorization policy and
// IRunTokenService; see Infrastructure/RunTokenService.cs.
builder.Services.AddAgentRunAuthentication(builder.Configuration);

// ---- Run watchdog: enforces maxDurationSeconds and approvals.expires_at ----
builder.Services.AddSingleton<RunWatchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunWatchdog>());

// ---- On-disk retention: orphaned plaintext secrets, expired run workspaces/artifacts ----
builder.Services.AddHostedService<RunDataJanitor>();

// ---- Trigger scheduler: fires cron triggers (lot 4) ----
// Registered on every replica: the claim is atomic (triggers.next_run_at), so several schedulers
// racing on the same due row is the normal case, not a misconfiguration. See TriggerScheduler.
builder.Services.AddHostedService<TriggerScheduler>();

// ---- SignalR ----
// AddJsonProtocol uses its own JsonSerializerOptions, separate from ConfigureHttpJsonOptions
// below — without this, hub payloads (Run/RunEvent broadcasts) would serialize enums as
// PascalCase while the REST API serializes them as snake_case. Keep both in sync.
var signalR = builder.Services.AddSignalR(options =>
    {
        // Makes the caller's identity visible to everything a hub method calls into; see
        // Hubs/CallerContextHubFilter.cs.
        options.AddFilter<CallerContextHubFilter>();
    })
    .AddHubOptions<RunHub>(options => options.MaximumReceiveMessageSize = 1_000_000)
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new RunStatusJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new AgentTypeJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new TriggeredByTypeJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new ApprovalTypeJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new ApprovalStatusJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new SecretScopeJsonConverter());
        options.PayloadSerializerOptions.Converters.Add(new UserRoleJsonConverter());
    });

// SignalR backplane. Without it, hub state is per-process: a client connected to replica A never
// sees an event published by replica B, so any deployment with more than one backend replica
// silently drops run events. Enabled whenever Redis is actually reachable, following the same
// optional-Redis pattern as the cache — an unconfigured/unreachable Redis degrades to the
// in-memory hub lifetime manager (correct for the single-replica default) rather than failing.
if (redisConnection is not null)
{
    signalR.AddStackExchangeRedis(options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("agenthost");
        options.ConnectionFactory = _ => Task.FromResult<IConnectionMultiplexer>(redisConnection);
    });
    Log.Logger.Information("SignalR Redis backplane enabled");
}
else
{
    Log.Logger.Information("SignalR running without a backplane (Redis not configured/reachable); " +
                           "this is only correct with a single backend replica");
}

// ---- CORS ----
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (allowedOrigins.Length == 0)
    allowedOrigins = new[] { "http://localhost:4200" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums serialize as the same lowercase/snake_case strings as the DB and the documented
    // API contract (e.g. RunStatus "awaiting_approval"), not System.Text.Json's default
    // PascalCase member names — see Infrastructure/EnumJsonConverters.cs.
    options.SerializerOptions.Converters.Add(new RunStatusJsonConverter());
    options.SerializerOptions.Converters.Add(new AgentTypeJsonConverter());
    options.SerializerOptions.Converters.Add(new TriggeredByTypeJsonConverter());
    options.SerializerOptions.Converters.Add(new ApprovalTypeJsonConverter());
    options.SerializerOptions.Converters.Add(new ApprovalStatusJsonConverter());
    options.SerializerOptions.Converters.Add(new SecretScopeJsonConverter());
    options.SerializerOptions.Converters.Add(new UserRoleJsonConverter());
});

builder.Services.AddEndpointsApiExplorer();

// ---- Forwarded headers ----
// The rate limiter's anonymous partition and every access log line use RemoteIpAddress. Behind the
// bundled nginx (or any ingress) that is the proxy's address, so without this every anonymous
// caller in the world shares one bucket. X-Forwarded-For/-Proto are trivially spoofable when they
// come from an untrusted peer, so they are only honoured from proxies we know about:
// ForwardedHeaders:KnownProxies / :KnownNetworks (CIDR). Empty defaults are deliberately
// permissive-for-loopback only — the framework already trusts 127.0.0.1/::1 — and a deployment
// behind a proxy on another host MUST list it, otherwise the headers are ignored (safe default:
// everyone shares the proxy bucket) rather than trusted blindly.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>())
    {
        if (System.Net.IPAddress.TryParse(proxy, out var address))
            options.KnownProxies.Add(address);
    }

    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>())
    {
        var parts = network.Split('/');
        if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var length))
            options.KnownNetworks.Add(new IPNetwork(prefix, length));
    }
});

// ---- Rate limiting ----
// Two chained partitions, both installed as the global limiter so a request must satisfy both:
//   * 120 requests/minute per identity for everything;
//   * a much tighter bucket on the unauthenticated auth endpoints, which are the brute-force
//     surface (credential stuffing against /api/auth/login costs an attacker nothing otherwise).
// Chaining rather than per-endpoint policies keeps this entirely in Program.cs and keeps the test
// factories' "clear GlobalLimiter to disable rate limiting" arrangement working.
builder.Services.AddRateLimiter(options =>
{
    var generalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Partition on the token's subject, not Identity.Name: our JWTs carry no `name` claim
            // (MapInboundClaims is off), so Identity.Name is always null and every authenticated
            // caller in the system silently shared a single IP-keyed bucket. GetUserId() reads the
            // `sub` claim — a user id for a user token, a run id for an agent run token — which is
            // the "per authenticated user" partition this limiter was documented to apply.
            //
            // NOTE: the limiter now runs BEFORE authentication (see the pipeline below), so
            // GetUserId() is only populated for endpoints that authenticated earlier in the
            // request... which is none of them. In practice this means the general limiter
            // partitions anonymous traffic by client IP and authenticated traffic by IP too. The
            // `sub` branch is kept because it is correct the moment authentication moves ahead of
            // the limiter again, and it is what agent run tokens partition on when it does.
            ctx.User.GetUserId() ?? ClientKey(ctx),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    var authLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        IsAuthEndpoint(ctx)
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"auth:{ClientKey(ctx)}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter<string>("auth:exempt"));

    options.GlobalLimiter = PartitionedRateLimiter.CreateChained(generalLimiter, authLimiter);
    options.OnRejected = (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
});

// ---- OpenTelemetry (spec section 14.2) ----
// Metrics are always collected and exposed on /metrics (cheap, in-process). Traces are only
// exported when OpenTelemetry:OtlpEndpoint is set, so dev and the test host stay no-op.
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: builder.Configuration["OpenTelemetry:ServiceName"] ?? "agenthost-backend",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(AgentHostMetrics.MeterName)
        .AddMeter("Npgsql")
        .AddPrometheusExporter())
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation(o => o.Filter = ctx =>
                // Probes and the scrape endpoint would otherwise dominate the trace volume.
                !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
            .AddHttpClientInstrumentation()
            .AddNpgsql();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });

// ---- Health checks ----
// "ready" is the tag that separates readiness (needs its dependencies) from liveness (process is
// running and the event loop responds); see the /health endpoints below.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" });

var app = builder.Build();

// ---- Startup migrations (idempotent; see Infrastructure/MigrationRunner) ----
using (var scope = app.Services.CreateScope())
{
    var migrationRunner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    try
    {
        await migrationRunner.RunAsync();
    }
    catch (Exception ex)
    {
        Log.Logger.Error(ex, "Startup migrations failed");
        throw;
    }
}

// ---- Mode maintenance : rechiffrement des secrets (rotation de clé) ----
//
// `dotnet AgentHost.Api.dll --rekey-secrets` réécrit tout le matériel chiffré avec
// Secrets:EncryptionKey, puis sort sans jamais écouter sur le réseau. C'est une opération
// d'exploitation portant sur l'instance entière — pas sur une organisation — donc elle n'a rien à
// faire derrière une API scopée par tenant : il n'y a pas d'appelant à autoriser, pas de délai de
// requête à respecter, et rien à exposer.
//
// Placé après les migrations pour que le schéma soit à jour, et avant tout middleware.
if (args.Contains("--rekey-secrets"))
{
    using var rekeyScope = app.Services.CreateScope();
    var rekey = rekeyScope.ServiceProvider.GetRequiredService<ISecretsRekeyService>();

    var report = await rekey.RekeyAllAsync();
    await Log.CloseAndFlushAsync();

    // Code de sortie non nul si quoi que ce soit a échoué : un script d'exploitation doit pouvoir
    // s'arrêter là plutôt que d'enchaîner sur le retrait de l'ancienne clé.
    return report.Failed == 0 ? 0 : 1;
}

// `dotnet AgentHost.Api.dll --purge-org <orgId>` efface DÉFINITIVEMENT une organisation et tout ce
// qui en dépend, puis sort sans jamais écouter sur le réseau (dette : « purge RGPD réelle »).
//
// Hors ligne pour les mêmes raisons que --rekey-secrets, plus une qui lui est propre : celui dont
// on efface l'organisation ne peut pas, par construction, rester authentifié à la fin de
// l'opération. Il n'y a donc pas d'appelant à autoriser, et une purge accessible par HTTP serait
// une suppression de compte à un jeton de distance.
if (args.Contains("--purge-org"))
{
    var index = Array.IndexOf(args, "--purge-org");
    var orgId = index + 1 < args.Length ? args[index + 1] : null;

    if (string.IsNullOrWhiteSpace(orgId) || orgId.StartsWith('-'))
    {
        Log.Error("Usage : --purge-org <orgId>");
        await Log.CloseAndFlushAsync();
        return 2;
    }

    using var purgeScope = app.Services.CreateScope();
    var purge = purgeScope.ServiceProvider.GetRequiredService<IOrgPurgeService>();
    var purgeReport = await purge.PurgeAsync(orgId);
    await Log.CloseAndFlushAsync();

    // Code distinct quand l'organisation n'existait pas : un script d'exploitation doit pouvoir
    // distinguer « effacée » de « il n'y avait rien à effacer », qui n'appellent pas la même suite.
    return purgeReport.Found ? 0 : 3;
}

// Must run before anything that reads the client IP or scheme (rate limiter partitions, logs).
app.UseForwardedHeaders();

app.UseCors("Frontend");

// Documented middleware order: UseRouting -> UseRateLimiter -> UseAuthentication -> UseAuthorization.
// The limiter used to sit after authorization, so an anonymous flood against a protected endpoint
// was 401'd before the limiter ever saw it — i.e. exactly the abuse it exists to stop was exempt
// from it. UseRouting is explicit here so the limiter can see endpoint metadata
// (DisableRateLimiting on /health, /metrics) even though minimal APIs would add it implicitly.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<RunHub>("/hubs/run").RequireAuthorization();
app.MapHub<ProjectHub>("/hubs/project").RequireAuthorization();
app.MapHub<AgentMemoryHub>("/hubs/memory").RequireAuthorization();

app.MapAuthEndpoints();
app.MapInvitationEndpoints();
app.MapRunEndpoints();
app.MapAgentProtocolEndpoints();
app.MapAgentEndpoints();
app.MapAgentVersionEndpoints();
app.MapArtifactEndpoints();
app.MapProjectEndpoints();
app.MapApprovalEndpoints();
app.MapMemoryEndpoints();
app.MapWebhookEndpoints();
app.MapTriggerEndpoints();
app.MapOrganizationEndpoints();
app.MapUserEndpoints();
app.MapAuditEndpoints();
app.MapMetricsEndpoints();
app.MapSecretEndpoints();

// ---- Health probes ----
// /health/live  — process is up and serving; no dependency is consulted, so a database outage
//                 never causes an orchestrator to restart otherwise-healthy pods.
// /health/ready — dependencies tagged "ready" (currently Postgres) are reachable; this is what
//                 load balancers should gate traffic on.
// /health       — kept as an alias of liveness so existing probes/compose files keep working.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
    .AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous().DisableRateLimiting();

// Prometheus scrape endpoint. It exposes internal operational detail (route names, in-flight
// requests, GC state), so it is NOT proxied by the bundled nginx — scrape the backend directly on
// its own port/network. See nginx.conf, which returns 404 for /metrics.
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous().DisableRateLimiting();

app.Run();

// Le mode --rekey-secrets rend plus haut ; ce retour est le chemin normal (app.Run bloque
// jusqu'à l'arrêt), et il donne au programme un type de retour cohérent.
return 0;

/// <summary>
/// Builds the artifact storage backend from <c>Artifacts:Provider</c> (<c>local</c> by default,
/// <c>s3</c> for AWS S3 / MinIO / Ceph). An unknown provider name is fatal rather than silently
/// falling back to local disk, which would write artifacts to a node's ephemeral filesystem while the
/// operator believes they are in a bucket.
/// </summary>
static IArtifactStorage CreateArtifactStorage(IConfiguration config, Serilog.ILogger logger)
{
    var provider = (config["Artifacts:Provider"] ?? "local").Trim().ToLowerInvariant();

    switch (provider)
    {
        case "" or "local":
            var local = new LocalArtifactStorage(config);
            logger.Information("Artifact storage: local disk at {Root}", local.Root);
            return local;

        case "s3":
            var options = S3ArtifactStorageOptions.FromConfiguration(config);
            var errors = options.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("Artifacts:Provider is 's3' but the configuration is incomplete: " + string.Join(" ", errors));

            logger.Information(
                "Artifact storage: S3 bucket {Bucket} (endpoint {Endpoint}, prefix {Prefix}, {Credentials} credentials)",
                options.Bucket, options.ServiceUrl ?? $"AWS {options.EffectiveRegion}", options.KeyPrefix,
                options.HasStaticCredentials ? "static" : "ambient");
            return new S3ArtifactStorage(options);

        default:
            throw new InvalidOperationException(
                $"Unknown Artifacts:Provider '{provider}'. Supported values: 'local', 's3'.");
    }
}

/// <summary>
/// Connects to Redis when configured, returning null when it is not configured or not reachable —
/// Redis is optional (spec 4.1), so neither case may block startup.
/// </summary>
static IConnectionMultiplexer? ConnectRedis(IConfiguration config, Serilog.ILogger logger)
{
    var redisHost = config["Redis:Host"];
    if (string.IsNullOrWhiteSpace(redisHost))
        return null;

    var redisPort = config["Redis:Port"] ?? "6379";
    try
    {
        var options = ConfigurationOptions.Parse($"{redisHost}:{redisPort}");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        var multiplexer = ConnectionMultiplexer.Connect(options);

        // AbortOnConnectFail=false means Connect() succeeds even against a dead server (it keeps
        // retrying in the background). Treat "not connected" as "no Redis": the SignalR backplane
        // must not be wired to a connection that has never worked, and the cache path already
        // null-checks the holder.
        if (!multiplexer.IsConnected)
        {
            logger.Warning("Redis at {Host}:{Port} did not connect; continuing without cache/backplane",
                redisHost, redisPort);
            multiplexer.Dispose();
            return null;
        }

        return multiplexer;
    }
    catch (Exception ex)
    {
        logger.Warning(ex, "Could not connect to Redis at {Host}:{Port}; continuing without cache/backplane",
            redisHost, redisPort);
        return null;
    }
}

/// <summary>Rate-limit partition key for a caller with no authenticated identity.</summary>
static string ClientKey(HttpContext ctx)
    => ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

/// <summary>True for the unauthenticated credential endpoints that get the stricter limit.</summary>
static bool IsAuthEndpoint(HttpContext ctx)
    => ctx.Request.Path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase);

/// <summary>Optional Redis connection wrapper; <see cref="Multiplexer"/> is null when Redis is not configured/reachable.</summary>
public class RedisConnectionHolder
{
    public IConnectionMultiplexer? Multiplexer { get; }
    public RedisConnectionHolder(IConnectionMultiplexer? multiplexer) => Multiplexer = multiplexer;
}

// Exposed for WebApplicationFactory-based integration tests, if any are added later.
public partial class Program { }
