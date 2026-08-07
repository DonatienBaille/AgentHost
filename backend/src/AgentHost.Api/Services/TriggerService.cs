using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface ITriggerService
{
    Task<CreateTriggerResponse> CreateAsync(CreateTriggerRequest req, string projectId, string orgId, string? actorUserId, CancellationToken ct = default);
    Task<List<Trigger>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default);
    Task<Trigger?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<Trigger?> UpdateAsync(string id, UpdateTriggerRequest req, string orgId, string? actorUserId, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, string orgId, string? actorUserId, CancellationToken ct = default);

    /// <summary>Traite une livraison entrante. Aucun appelant authentifié : c'est la signature qui autorise.</summary>
    Task<TriggerIngestResult> IngestAsync(string triggerId, byte[] body, Func<string, string?> header, CancellationToken ct = default);

    /// <summary>Lance le run d'un déclencheur. Utilisé par l'ingestion et par le planificateur.</summary>
    Task<Run?> FireAsync(Trigger trigger, TriggeredByType triggeredBy, JsonObject context, CancellationToken ct = default);
}

/// <summary>
/// Les déclencheurs entrants : gestion, ingestion des webhooks, déclenchement (lot 4).
///
/// <b>Ce qui change par rapport à tout le reste du dépôt.</b> Partout ailleurs, l'autorisation
/// vient du JWT. Ici, le chemin d'ingestion n'a pas de JWT du tout : c'est un serveur tiers qui
/// appelle, et ce qui l'autorise est la preuve qu'il détient le secret du déclencheur. Toute la
/// prudence de ce fichier découle de là — refus par défaut, secret jamais relu en clair, réponses
/// qui ne distinguent pas « ce déclencheur n'existe pas » de « il ne vous appartient pas ».
///
/// <b>Le secret est généré ici et rendu une seule fois.</b> Le laisser choisir par l'appelant
/// autoriserait un secret faible, et le relire à volonté ramènerait sa protection au niveau de
/// l'autorisation de lecture. Il est stocké chiffré par le même <see cref="ISecretsBroker"/> que
/// les secrets applicatifs, donc couvert par la rotation de clé.
/// </summary>
public class TriggerService : ITriggerService
{
    /// <summary>32 octets d'aléa, rendus en base64url : 256 bits, ce qui clôt la question.</summary>
    private const int SecretBytes = 32;

    private readonly ITriggerRepository _repository;
    private readonly IAgentService _agentService;
    private readonly IRunService _runService;
    private readonly ISecretsBroker _secretsBroker;
    private readonly IAuditService _auditService;
    private readonly ILogger _logger;

    public TriggerService(
        ITriggerRepository repository,
        IAgentService agentService,
        IRunService runService,
        ISecretsBroker secretsBroker,
        IAuditService auditService,
        ILogger logger)
    {
        _repository = repository;
        _agentService = agentService;
        _runService = runService;
        _secretsBroker = secretsBroker;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<CreateTriggerResponse> CreateAsync(
        CreateTriggerRequest req, string projectId, string orgId, string? actorUserId, CancellationToken ct = default)
    {
        var type = ParseType(req.Type);

        // L'agent est relu et confronté au périmètre de l'appelant AVANT toute écriture.
        //
        // Les deux conditions comptent, et pour des raisons différentes. L'organisation empêche de
        // poser un déclencheur sur l'agent d'autrui — ce qui reviendrait à pouvoir lancer des runs
        // sur son budget. Le projet de la route doit ensuite être celui de l'agent : les vérifier
        // ici plutôt qu'après l'insertion évite de créer une ligne pour la désavouer ensuite, ce
        // qui laisserait un déclencheur bien réel derrière une réponse 404.
        var agent = await _agentService.GetAsync(req.AgentId, ct);
        if (agent is null || agent.OrgId != orgId || agent.ProjectId != projectId)
            throw new KeyNotFoundException($"Agent {req.AgentId} not found");

        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("A trigger name is required");

        var now = DateTime.UtcNow;
        var trigger = new Trigger
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = orgId,
            ProjectId = agent.ProjectId,
            AgentId = agent.Id,
            Type = type,
            Name = req.Name.Trim(),
            IsActive = true,
            Inputs = req.Inputs ?? new JsonObject(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        string? secret = null;

        if (type == TriggerType.Webhook)
        {
            trigger.Provider = ParseProvider(req.Provider);
            trigger.EventFilter = new TriggerEventFilter
            {
                Events = Clean(req.Events),
                Branches = Clean(req.Branches),
            };

            secret = GenerateSecret();
            trigger.SecretEncrypted = _secretsBroker.Encrypt(secret);
        }
        else
        {
            var (schedule, timeZone) = ParseSchedule(req.CronExpression, req.TimeZone);
            trigger.CronExpression = schedule.Expression;
            trigger.TimeZone = timeZone.Id;
            // L'échéance est calculée à l'écriture, pas à la lecture : le planificateur compare une
            // date indexée au lieu de réévaluer toutes les expressions de l'installation.
            trigger.NextRunAt = schedule.GetNextOccurrence(now, timeZone);
        }

        await _repository.InsertAsync(trigger, ct);
        await _auditService.RecordAsync(
            orgId, "trigger.created", actorUserId, "trigger", trigger.Id,
            details: new JsonObject
            {
                ["type"] = type.ToDbString(),
                ["agentId"] = agent.Id,
                ["schedule"] = trigger.CronExpression,
                ["provider"] = trigger.Provider?.ToDbString(),
            },
            ct: ct);

        return new CreateTriggerResponse { Trigger = TriggerResponse.From(trigger), Secret = secret };
    }

    public Task<List<Trigger>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default) =>
        _repository.ListByProjectAsync(projectId, orgId, ct);

    public Task<Trigger?> GetAsync(string id, string orgId, CancellationToken ct = default) =>
        _repository.GetAsync(id, orgId, ct);

    public async Task<Trigger?> UpdateAsync(
        string id, UpdateTriggerRequest req, string orgId, string? actorUserId, CancellationToken ct = default)
    {
        var trigger = await _repository.GetAsync(id, orgId, ct);
        if (trigger is null) return null;

        var changes = new JsonObject();

        if (!string.IsNullOrWhiteSpace(req.Name)) trigger.Name = req.Name.Trim();

        if (req.IsActive is not null && req.IsActive != trigger.IsActive)
        {
            changes["isActive"] = new JsonObject { ["from"] = trigger.IsActive, ["to"] = req.IsActive.Value };
            trigger.IsActive = req.IsActive.Value;
        }

        if (req.Inputs is not null) trigger.Inputs = req.Inputs;

        if (trigger.Type == TriggerType.Webhook && (req.Events is not null || req.Branches is not null))
        {
            trigger.EventFilter = new TriggerEventFilter
            {
                Events = Clean(req.Events) is { Count: > 0 } e ? e : trigger.EventFilter?.Events ?? [],
                Branches = Clean(req.Branches) is { Count: > 0 } b ? b : trigger.EventFilter?.Branches ?? [],
            };
        }

        if (trigger.Type == TriggerType.Cron)
        {
            var expression = req.CronExpression ?? trigger.CronExpression;
            var zone = req.TimeZone ?? trigger.TimeZone;

            if (req.CronExpression is not null || req.TimeZone is not null)
            {
                var (schedule, timeZone) = ParseSchedule(expression, zone);
                changes["cron"] = new JsonObject
                {
                    ["from"] = trigger.CronExpression,
                    ["to"] = schedule.Expression,
                    ["timeZone"] = timeZone.Id,
                };
                trigger.CronExpression = schedule.Expression;
                trigger.TimeZone = timeZone.Id;
                trigger.NextRunAt = schedule.GetNextOccurrence(DateTime.UtcNow, timeZone);
            }
        }

        // Désactiver, c'est effacer l'échéance : la laisser en place ferait repartir le
        // déclencheur au réveil, avec toutes les occurrences manquées d'un coup.
        if (!trigger.IsActive) trigger.NextRunAt = null;
        else if (trigger.Type == TriggerType.Cron && trigger.NextRunAt is null && trigger.CronExpression is not null)
        {
            var (schedule, timeZone) = ParseSchedule(trigger.CronExpression, trigger.TimeZone);
            trigger.NextRunAt = schedule.GetNextOccurrence(DateTime.UtcNow, timeZone);
        }

        trigger.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateAsync(trigger, ct);

        await _auditService.RecordAsync(
            orgId, "trigger.updated", actorUserId, "trigger", trigger.Id,
            changes: changes.Count > 0 ? changes : null, ct: ct);

        return trigger;
    }

    public async Task<bool> DeleteAsync(string id, string orgId, string? actorUserId, CancellationToken ct = default)
    {
        var deleted = await _repository.SoftDeleteAsync(id, orgId, ct);
        if (deleted)
            await _auditService.RecordAsync(orgId, "trigger.deleted", actorUserId, "trigger", id, ct: ct);
        return deleted;
    }

    public async Task<TriggerIngestResult> IngestAsync(
        string triggerId, byte[] body, Func<string, string?> header, CancellationToken ct = default)
    {
        var trigger = await _repository.GetForIngestUnscopedAsync(triggerId, ct);
        // Inexistant, supprimé ou désactivé : une seule réponse pour les trois. Distinguer
        // « désactivé » de « inconnu » confirmerait l'existence d'un déclencheur à qui ne le
        // connaît pas.
        if (trigger?.SecretEncrypted is null || trigger.Provider is null)
            return new TriggerIngestResult { Outcome = TriggerIngestOutcome.UnknownTrigger };

        var secret = _secretsBroker.Decrypt(trigger.SecretEncrypted);
        var verdict = IncomingWebhookProtocol.Verify(trigger.Provider.Value, secret, body, header);
        if (verdict != SignatureVerdict.Valid)
        {
            // Journalisé parce qu'une signature fausse est le seul signal qu'on ait d'une
            // tentative de forge — l'absence de signature, elle, n'est souvent qu'une mauvaise
            // configuration.
            _logger.Warning("Rejected webhook delivery for trigger {TriggerId}: signature {Verdict}",
                trigger.Id, verdict);
            return new TriggerIngestResult
            {
                Outcome = TriggerIngestOutcome.Unauthorized,
                Reason = verdict == SignatureVerdict.Missing ? "signature_missing" : "signature_invalid",
            };
        }

        var deliveryId = IncomingWebhookProtocol.DeliveryId(trigger.Provider.Value, body, header);

        // Enregistré AVANT le filtre et avant le lancement : une réémission ne doit pas relancer
        // l'agent, y compris quand la première livraison a été écartée par le filtre.
        if (!await _repository.TryRecordDeliveryAsync(trigger.Id, deliveryId, ct))
        {
            _logger.Information("Ignoring replayed delivery {DeliveryId} for trigger {TriggerId}",
                deliveryId, trigger.Id);
            return new TriggerIngestResult { Outcome = TriggerIngestOutcome.Duplicate };
        }

        var payload = ParsePayload(body);
        var eventName = IncomingWebhookProtocol.EventName(trigger.Provider.Value, header);
        var branch = IncomingWebhookProtocol.BranchOf(payload);

        if (!IncomingWebhookProtocol.Accepts(trigger.EventFilter, eventName, branch))
            return new TriggerIngestResult { Outcome = TriggerIngestOutcome.Filtered };

        // Le contexte du run porte l'origine de la livraison. C'est ce que l'agent lit pour savoir
        // sur quoi il travaille — sans lui, un agent déclenché par un push ignorerait quel push.
        var context = new JsonObject
        {
            ["trigger"] = new JsonObject
            {
                ["id"] = trigger.Id,
                ["name"] = trigger.Name,
                ["type"] = trigger.Type.ToDbString(),
                ["provider"] = trigger.Provider.Value.ToDbString(),
                ["event"] = eventName,
                ["branch"] = branch,
                ["deliveryId"] = deliveryId,
            },
            // La charge utile complète, telle que reçue : c'est la matière de l'agent.
            ["payload"] = payload,
        };

        var run = await FireAsync(trigger, TriggeredByType.Webhook, context, ct);
        if (run is null)
            return new TriggerIngestResult { Outcome = TriggerIngestOutcome.Rejected, Reason = "run_rejected" };

        await _repository.AttachDeliveryRunAsync(trigger.Id, deliveryId, run.Id, ct);
        return new TriggerIngestResult { Outcome = TriggerIngestOutcome.Launched, RunId = run.Id };
    }

    public async Task<Run?> FireAsync(
        Trigger trigger, TriggeredByType triggeredBy, JsonObject context, CancellationToken ct = default)
    {
        try
        {
            var run = await _runService.CreateAsync(new CreateRunRequest
            {
                AgentId = trigger.AgentId,
                Inputs = trigger.Inputs?.DeepClone() ?? new JsonObject(),
                Context = context,
                TriggeredByType = triggeredBy,
            }, ct);

            await _repository.RecordFiringAsync(trigger.Id, run.Id, DateTime.UtcNow, ct);
            await _auditService.RecordAsync(
                trigger.OrgId, "trigger.fired", null, "trigger", trigger.Id,
                details: new JsonObject { ["runId"] = run.Id, ["triggeredBy"] = triggeredBy.ToDbString() },
                ct: ct);

            return run;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            // Un agent sans version publiée, un budget mensuel épuisé : ce sont des refus
            // légitimes et attendus, pas des pannes. Ils ne doivent ni faire tomber le
            // planificateur ni faire répondre 500 à une forge, qui réessaierait indéfiniment.
            _logger.Warning(ex, "Trigger {TriggerId} could not launch a run", trigger.Id);
            return null;
        }
    }

    // ---- helpers ----

    private static string GenerateSecret() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(SecretBytes));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static TriggerType ParseType(string value) => value?.ToLowerInvariant() switch
    {
        "webhook" => TriggerType.Webhook,
        "cron" => TriggerType.Cron,
        _ => throw new ArgumentException("Trigger type must be 'webhook' or 'cron'"),
    };

    private static TriggerProvider ParseProvider(string? value) => value?.ToLowerInvariant() switch
    {
        // Le défaut est le format le plus sûr des trois, pas le plus permissif.
        null or "" or "generic" => TriggerProvider.Generic,
        "github" => TriggerProvider.GitHub,
        "gitlab" => TriggerProvider.GitLab,
        _ => throw new ArgumentException("Trigger provider must be 'github', 'gitlab' or 'generic'"),
    };

    /// <summary>Valide expression et fuseau ensemble : une planification incomplète n'est pas persistée.</summary>
    private static (CronSchedule Schedule, TimeZoneInfo TimeZone) ParseSchedule(string? expression, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("A cron trigger needs a cronExpression");

        CronSchedule schedule;
        try
        {
            schedule = CronSchedule.Parse(expression);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(ex.Message);
        }

        var id = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        try
        {
            return (schedule, TimeZoneInfo.FindSystemTimeZoneById(id));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Rejeté à la création plutôt que d'échouer silencieusement au premier réveil, des
            // heures plus tard, dans un journal que personne ne lit.
            throw new ArgumentException($"Unknown time zone '{id}'");
        }
    }

    private static List<string> Clean(List<string>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList() ?? [];

    /// <summary>
    /// La charge utile, ou null si ce n'est pas du JSON.
    ///
    /// Une charge illisible n'annule pas la livraison : la signature est déjà vérifiée, donc
    /// l'émetteur est légitime. Le filtre de branches ne s'appliquera simplement pas.
    /// </summary>
    private static JsonNode? ParsePayload(byte[] body)
    {
        if (body.Length == 0) return null;
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(body));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
