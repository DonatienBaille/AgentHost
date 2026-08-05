using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AgentHost.Api.Endpoints;
using AgentHost.Api.Hubs;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Docker.DotNet;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.AddScoped<ICallerContext, CallerContext>();

builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton(sp => new MigrationRunner(
    builder.Configuration, sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<Serilog.ILogger>()));

builder.Services.AddSingleton<DockerClient>(_ =>
{
    var dockerHost = builder.Configuration["Docker:Host"] ?? "unix:///var/run/docker.sock";
    return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
});

// Redis is optional (spec 4.1: "optional, cache/sessions") — wrapped in a holder so a
// missing/unreachable Redis never blocks startup; consumers check RedisConnectionHolder.Multiplexer
// for null before use.
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<Serilog.ILogger>();
    var redisHost = builder.Configuration["Redis:Host"];
    if (string.IsNullOrWhiteSpace(redisHost))
        return new RedisConnectionHolder(null);

    var redisPort = builder.Configuration["Redis:Port"] ?? "6379";
    try
    {
        var options = ConfigurationOptions.Parse($"{redisHost}:{redisPort}");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 2000;
        return new RedisConnectionHolder(ConnectionMultiplexer.Connect(options));
    }
    catch (Exception ex)
    {
        logger.Warning(ex, "Could not connect to Redis at {Host}:{Port}; continuing without cache", redisHost, redisPort);
        return new RedisConnectionHolder(null);
    }
});

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

// ---- Services ----
builder.Services.AddScoped<IRunService, RunService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
builder.Services.AddScoped<IContainerOrchestrator, ContainerOrchestrator>();
builder.Services.AddScoped<IEventBus, SignalREventBus>();
builder.Services.AddScoped<RunStateMachine>();
builder.Services.AddScoped<ISecretsBroker, SecretsBroker>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
builder.Services.AddSingleton<IAgentManifestParser, AgentManifestParser>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddHttpClient();

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

// ---- SignalR ----
// AddJsonProtocol uses its own JsonSerializerOptions, separate from ConfigureHttpJsonOptions
// below — without this, hub payloads (Run/RunEvent broadcasts) would serialize enums as
// PascalCase while the REST API serializes them as snake_case. Keep both in sync.
builder.Services.AddSignalR()
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

// ---- Rate limiting: 120 requests/minute per authenticated user (falls back to client IP) ----
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Partition on the token's subject, not Identity.Name: our JWTs carry no `name` claim
            // (MapInboundClaims is off), so Identity.Name is always null and every authenticated
            // caller in the system silently shared a single IP-keyed bucket. GetUserId() reads the
            // `sub` claim — a user id for a user token, a run id for an agent run token — which is
            // the "per authenticated user" partition this limiter was documented to apply.
            ctx.User.GetUserId() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
});

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

app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHub<RunHub>("/hubs/run").RequireAuthorization();
app.MapHub<ProjectHub>("/hubs/project").RequireAuthorization();
app.MapHub<AgentMemoryHub>("/hubs/memory").RequireAuthorization();

app.MapAuthEndpoints();
app.MapRunEndpoints();
app.MapAgentProtocolEndpoints();
app.MapAgentEndpoints();
app.MapAgentVersionEndpoints();
app.MapArtifactEndpoints();
app.MapProjectEndpoints();
app.MapApprovalEndpoints();
app.MapMemoryEndpoints();
app.MapWebhookEndpoints();
app.MapOrganizationEndpoints();
app.MapUserEndpoints();
app.MapAuditEndpoints();
app.MapSecretEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous().DisableRateLimiting();

app.Run();

/// <summary>Optional Redis connection wrapper; <see cref="Multiplexer"/> is null when Redis is not configured/reachable.</summary>
public class RedisConnectionHolder
{
    public IConnectionMultiplexer? Multiplexer { get; }
    public RedisConnectionHolder(IConnectionMultiplexer? multiplexer) => Multiplexer = multiplexer;
}

// Exposed for WebApplicationFactory-based integration tests, if any are added later.
public partial class Program { }
