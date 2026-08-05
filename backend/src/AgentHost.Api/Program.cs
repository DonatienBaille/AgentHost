using System.Text.Json.Serialization;
using AgentHost.Api.Endpoints;
using AgentHost.Api.Hubs;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Docker.DotNet;
using FluentValidation;
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
builder.Services.AddSingleton<IAgentManifestParser, AgentManifestParser>();

// ---- Validation ----
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// ---- SignalR ----
builder.Services.AddSignalR()
    .AddHubOptions<RunHub>(options => options.MaximumReceiveMessageSize = 1_000_000);

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
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddEndpointsApiExplorer();

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

app.MapHub<RunHub>("/hubs/run");
app.MapHub<ProjectHub>("/hubs/project");
app.MapHub<AgentMemoryHub>("/hubs/memory");

app.MapRunEndpoints();
app.MapAgentEndpoints();
app.MapProjectEndpoints();
app.MapApprovalEndpoints();
app.MapMemoryEndpoints();
app.MapWebhookEndpoints();
app.MapOrganizationEndpoints();
app.MapUserEndpoints();
app.MapAuditEndpoints();
app.MapSecretEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

/// <summary>Optional Redis connection wrapper; <see cref="Multiplexer"/> is null when Redis is not configured/reachable.</summary>
public class RedisConnectionHolder
{
    public IConnectionMultiplexer? Multiplexer { get; }
    public RedisConnectionHolder(IConnectionMultiplexer? multiplexer) => Multiplexer = multiplexer;
}

// Exposed for WebApplicationFactory-based integration tests, if any are added later.
public partial class Program { }
