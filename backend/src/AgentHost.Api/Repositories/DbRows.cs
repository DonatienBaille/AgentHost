using System.Text.Json.Nodes;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Repositories;

/// <summary>
/// Database row shapes for the tables that have enum-typed columns.
///
/// Dapper cannot be trusted with a CLR enum in either direction:
///   * writing, it short-circuits an enum parameter to its underlying integral type *before*
///     consulting a registered <c>SqlMapper.TypeHandler</c>, so `RunStatus.InfraError` is bound
///     as the ordinal 14 and the column ends up holding the text '14';
///   * reading, it routes a string column into an enum member via <c>Enum.Parse</c>, which cannot
///     produce <c>RunStatus.AwaitingApproval</c> from 'awaiting_approval' (there is no member by
///     that name) but *will* happily parse '14' straight back to the member at position 14 —
///     which is exactly why the ordinal encoding round-tripped for so long without anyone noticing.
///
/// The fix is to never hand Dapper an enum at all. These rows type every enum column as
/// <see cref="string"/>, and the conversion to/from the domain model happens in ordinary C# via
/// each enum's <c>ToDbString()</c>/<c>FromDbString()</c>. Repositories query the row type and call
/// <c>ToDomain()</c>; they pass <c>FromDomain(...)</c> as the parameter object when writing. A
/// repository method that forgets and binds the domain object directly will now be caught by the
/// CHECK constraints added in migrations/0004_enum_string_encoding.sql, which reject the ordinal
/// form outright.
///
/// Non-enum columns keep their natural types — in particular the jsonb columns stay as
/// <c>JsonNode</c>/<c>List&lt;T&gt;</c> so <see cref="Infrastructure.JsonTypeHandler{T}"/> (which
/// works, because those are not enums) still does the serialization.
/// </summary>
internal sealed class RunRow
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public long Number { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string AgentVersionId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public JsonNode? Inputs { get; set; }
    public JsonNode? Context { get; set; }
    public JsonNode? Outputs { get; set; }

    public string? WorkspacePath { get; set; }
    public long? DurationMs { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
    public decimal? BudgetMaxUsd { get; set; }
    public decimal? BudgetUsedUsd { get; set; }

    public string? TriggeredByUserId { get; set; }
    public string? TriggeredByType { get; set; }

    public string? ParentRunId { get; set; }
    public string? RootRunId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public static RunRow FromDomain(Run run) => new()
    {
        Id = run.Id,
        OrgId = run.OrgId,
        ProjectId = run.ProjectId,
        Number = run.Number,
        AgentId = run.AgentId,
        AgentVersionId = run.AgentVersionId,
        Status = run.Status.ToDbString(),
        Inputs = run.Inputs,
        Context = run.Context,
        Outputs = run.Outputs,
        WorkspacePath = run.WorkspacePath,
        DurationMs = run.DurationMs,
        ExitCode = run.ExitCode,
        ErrorMessage = run.ErrorMessage,
        ErrorCode = run.ErrorCode,
        BudgetMaxUsd = run.BudgetMaxUsd,
        BudgetUsedUsd = run.BudgetUsedUsd,
        TriggeredByUserId = run.TriggeredByUserId,
        TriggeredByType = run.TriggeredByType.ToDbString(),
        ParentRunId = run.ParentRunId,
        RootRunId = run.RootRunId,
        CreatedAt = run.CreatedAt,
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        UpdatedAt = run.UpdatedAt,
        DeletedAt = run.DeletedAt,
    };

    public Run ToDomain() => new()
    {
        Id = Id,
        OrgId = OrgId,
        ProjectId = ProjectId,
        Number = Number,
        AgentId = AgentId,
        AgentVersionId = AgentVersionId,
        Status = RunStatusExtensions.FromDbString(Status),
        Inputs = Inputs ?? new JsonObject(),
        Context = Context ?? new JsonObject(),
        Outputs = Outputs,
        WorkspacePath = WorkspacePath,
        DurationMs = DurationMs,
        ExitCode = ExitCode,
        ErrorMessage = ErrorMessage,
        ErrorCode = ErrorCode,
        BudgetMaxUsd = BudgetMaxUsd,
        BudgetUsedUsd = BudgetUsedUsd,
        TriggeredByUserId = TriggeredByUserId,
        TriggeredByType = TriggeredByType is null
            ? Domain.TriggeredByType.Manual
            : TriggeredByTypeExtensions.FromDbString(TriggeredByType),
        ParentRunId = ParentRunId,
        RootRunId = RootRunId,
        CreatedAt = CreatedAt,
        StartedAt = StartedAt,
        FinishedAt = FinishedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt,
    };
}

/// <summary>Row shape for <c>agents</c> (agent_type). See <see cref="RunRow"/> for why this exists.</summary>
internal sealed class AgentRow
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    public string AgentType { get; set; } = string.Empty;

    public string? ImageRef { get; set; }
    public string ManifestYaml { get; set; } = string.Empty;
    public string InputsSchema { get; set; } = "{}";
    public string? OutputsSchema { get; set; }
    public string? CurrentVersionId { get; set; }
    public bool IsPublished { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public static AgentRow FromDomain(Agent agent) => new()
    {
        Id = agent.Id,
        OrgId = agent.OrgId,
        ProjectId = agent.ProjectId,
        Name = agent.Name,
        Slug = agent.Slug,
        AgentType = agent.AgentType.ToDbString(),
        ImageRef = agent.ImageRef,
        ManifestYaml = agent.ManifestYaml,
        InputsSchema = agent.InputsSchema,
        OutputsSchema = agent.OutputsSchema,
        CurrentVersionId = agent.CurrentVersionId,
        IsPublished = agent.IsPublished,
        CreatedAt = agent.CreatedAt,
        UpdatedAt = agent.UpdatedAt,
        DeletedAt = agent.DeletedAt,
    };

    public Agent ToDomain() => new()
    {
        Id = Id,
        OrgId = OrgId,
        ProjectId = ProjectId,
        Name = Name,
        Slug = Slug,
        AgentType = AgentTypeExtensions.FromDbString(AgentType),
        ImageRef = ImageRef,
        ManifestYaml = ManifestYaml,
        InputsSchema = InputsSchema,
        OutputsSchema = OutputsSchema,
        CurrentVersionId = CurrentVersionId,
        IsPublished = IsPublished,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt,
    };
}

/// <summary>Row shape for <c>approvals</c> (approval_type, status). See <see cref="RunRow"/>.</summary>
internal sealed class ApprovalRow
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string? StepId { get; set; }

    public string ApprovalType { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;
    public JsonNode? Options { get; set; }

    /// <summary>Free-text role name, not a persisted enum column — kept as-is.</summary>
    public string? RequiredRole { get; set; }

    public int RequiredCount { get; set; }
    public List<ApprovalResponse>? Responses { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public static ApprovalRow FromDomain(Approval approval) => new()
    {
        Id = approval.Id,
        RunId = approval.RunId,
        StepId = approval.StepId,
        ApprovalType = approval.ApprovalType.ToDbString(),
        Prompt = approval.Prompt,
        Options = approval.Options,
        RequiredRole = approval.RequiredRole,
        RequiredCount = approval.RequiredCount,
        Responses = approval.Responses,
        Status = approval.Status.ToDbString(),
        ExpiresAt = approval.ExpiresAt,
        DecidedAt = approval.DecidedAt,
        DecidedBy = approval.DecidedBy,
        CreatedAt = approval.CreatedAt,
    };

    public Approval ToDomain() => new()
    {
        Id = Id,
        RunId = RunId,
        StepId = StepId,
        ApprovalType = ApprovalTypeExtensions.FromDbString(ApprovalType),
        Prompt = Prompt,
        Options = Options,
        RequiredRole = RequiredRole,
        RequiredCount = RequiredCount,
        Responses = Responses ?? new List<ApprovalResponse>(),
        Status = ApprovalStatusExtensions.FromDbString(Status),
        ExpiresAt = ExpiresAt,
        DecidedAt = DecidedAt,
        DecidedBy = DecidedBy,
        CreatedAt = CreatedAt,
    };
}

/// <summary>Row shape for <c>secrets</c> (scope). See <see cref="RunRow"/>.</summary>
internal sealed class SecretRow
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;

    public byte[]? EncryptedValue { get; set; }
    public string? VaultPath { get; set; }
    public string? DigestSha256Truncated { get; set; }

    public string Scope { get; set; } = string.Empty;

    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedByRunId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public static SecretRow FromDomain(Secret secret) => new()
    {
        Id = secret.Id,
        OrgId = secret.OrgId,
        ProjectId = secret.ProjectId,
        Name = secret.Name,
        EncryptedValue = secret.EncryptedValue,
        VaultPath = secret.VaultPath,
        DigestSha256Truncated = secret.DigestSha256Truncated,
        Scope = secret.Scope.ToDbString(),
        LastUsedAt = secret.LastUsedAt,
        LastUsedByRunId = secret.LastUsedByRunId,
        CreatedAt = secret.CreatedAt,
        UpdatedAt = secret.UpdatedAt,
        DeletedAt = secret.DeletedAt,
    };

    public Secret ToDomain() => new()
    {
        Id = Id,
        OrgId = OrgId,
        ProjectId = ProjectId,
        Name = Name,
        EncryptedValue = EncryptedValue,
        VaultPath = VaultPath,
        DigestSha256Truncated = DigestSha256Truncated,
        Scope = SecretScopeExtensions.FromDbString(Scope),
        LastUsedAt = LastUsedAt,
        LastUsedByRunId = LastUsedByRunId,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt,
    };
}

/// <summary>Row shape for <c>users</c> (role). See <see cref="RunRow"/>.</summary>
internal sealed class UserRow
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }

    public string Role { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public static UserRow FromDomain(User user) => new()
    {
        Id = user.Id,
        OrgId = user.OrgId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        AvatarUrl = user.AvatarUrl,
        Role = user.Role.ToDbString(),
        PasswordHash = user.PasswordHash,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        DeletedAt = user.DeletedAt,
    };

    public User ToDomain() => new()
    {
        Id = Id,
        OrgId = OrgId,
        Email = Email,
        DisplayName = DisplayName,
        AvatarUrl = AvatarUrl,
        Role = UserRoleExtensions.FromDbString(Role),
        PasswordHash = PasswordHash,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt,
    };
}
