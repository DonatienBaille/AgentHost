namespace AgentHost.Api.Domain;

/// <summary>
/// Run lifecycle status (spec section 8.1). String values match the lowercase/snake-case
/// values stored in the `runs.status` column.
/// </summary>
public enum RunStatus
{
    Pending,
    Queued,
    Provisioning,
    Preparing,
    Running,
    AwaitingApproval,
    AwaitingInput,
    Finalizing,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    BudgetExceeded,
    Rejected,
    InfraError,
}

public static class RunStatusExtensions
{
    /// <summary>
    /// Terminal states per spec 8.1: succeeded, failed, cancelled, timed_out,
    /// budget_exceeded, rejected, infra_error. All others are non-terminal.
    /// </summary>
    public static bool IsTerminal(this RunStatus status) => status switch
    {
        RunStatus.Succeeded => true,
        RunStatus.Failed => true,
        RunStatus.Cancelled => true,
        RunStatus.TimedOut => true,
        RunStatus.BudgetExceeded => true,
        RunStatus.Rejected => true,
        RunStatus.InfraError => true,
        _ => false,
    };

    public static string ToDbString(this RunStatus status) => status switch
    {
        RunStatus.Pending => "pending",
        RunStatus.Queued => "queued",
        RunStatus.Provisioning => "provisioning",
        RunStatus.Preparing => "preparing",
        RunStatus.Running => "running",
        RunStatus.AwaitingApproval => "awaiting_approval",
        RunStatus.AwaitingInput => "awaiting_input",
        RunStatus.Finalizing => "finalizing",
        RunStatus.Succeeded => "succeeded",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        RunStatus.TimedOut => "timed_out",
        RunStatus.BudgetExceeded => "budget_exceeded",
        RunStatus.Rejected => "rejected",
        RunStatus.InfraError => "infra_error",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static RunStatus FromDbString(string value) => value switch
    {
        "pending" => RunStatus.Pending,
        "queued" => RunStatus.Queued,
        "provisioning" => RunStatus.Provisioning,
        "preparing" => RunStatus.Preparing,
        "running" => RunStatus.Running,
        "awaiting_approval" => RunStatus.AwaitingApproval,
        "awaiting_input" => RunStatus.AwaitingInput,
        "finalizing" => RunStatus.Finalizing,
        "succeeded" => RunStatus.Succeeded,
        "failed" => RunStatus.Failed,
        "cancelled" => RunStatus.Cancelled,
        "timed_out" => RunStatus.TimedOut,
        "budget_exceeded" => RunStatus.BudgetExceeded,
        "rejected" => RunStatus.Rejected,
        "infra_error" => RunStatus.InfraError,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown run status"),
    };
}

/// <summary>Agent implementation kind (spec section 6.1).</summary>
public enum AgentType
{
    Oci,
    Copilot,
    ClaudeCode,
    OpenAi,
    Custom,
}

public static class AgentTypeExtensions
{
    public static string ToDbString(this AgentType type) => type switch
    {
        AgentType.Oci => "oci",
        AgentType.Copilot => "copilot",
        AgentType.ClaudeCode => "claude_code",
        AgentType.OpenAi => "openai",
        AgentType.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static AgentType FromDbString(string value) => value switch
    {
        "oci" => AgentType.Oci,
        "copilot" => AgentType.Copilot,
        "claude_code" => AgentType.ClaudeCode,
        "openai" => AgentType.OpenAi,
        "custom" => AgentType.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown agent type"),
    };
}

/// <summary>How a run was triggered (runs.triggered_by_type).</summary>
public enum TriggeredByType
{
    Manual,
    Webhook,
    Cron,
    Api,
    Chain,
}

public static class TriggeredByTypeExtensions
{
    public static string ToDbString(this TriggeredByType type) => type switch
    {
        TriggeredByType.Manual => "manual",
        TriggeredByType.Webhook => "webhook",
        TriggeredByType.Cron => "cron",
        TriggeredByType.Api => "api",
        TriggeredByType.Chain => "chain",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static TriggeredByType FromDbString(string value) => value switch
    {
        "manual" => TriggeredByType.Manual,
        "webhook" => TriggeredByType.Webhook,
        "cron" => TriggeredByType.Cron,
        "api" => TriggeredByType.Api,
        "chain" => TriggeredByType.Chain,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown trigger type"),
    };
}

/// <summary>Approval kind (approvals.approval_type).</summary>
public enum ApprovalType
{
    Gate,
    Question,
    BudgetIncrease,
}

public static class ApprovalTypeExtensions
{
    public static string ToDbString(this ApprovalType type) => type switch
    {
        ApprovalType.Gate => "gate",
        ApprovalType.Question => "question",
        ApprovalType.BudgetIncrease => "budget_increase",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ApprovalType FromDbString(string value) => value switch
    {
        "gate" => ApprovalType.Gate,
        "question" => ApprovalType.Question,
        "budget_increase" => ApprovalType.BudgetIncrease,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown approval type"),
    };
}

/// <summary>Approval status (approvals.status).</summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

public static class ApprovalStatusExtensions
{
    public static string ToDbString(this ApprovalStatus status) => status switch
    {
        ApprovalStatus.Pending => "pending",
        ApprovalStatus.Approved => "approved",
        ApprovalStatus.Rejected => "rejected",
        ApprovalStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static ApprovalStatus FromDbString(string value) => value switch
    {
        "pending" => ApprovalStatus.Pending,
        "approved" => ApprovalStatus.Approved,
        "rejected" => ApprovalStatus.Rejected,
        "expired" => ApprovalStatus.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown approval status"),
    };
}

/// <summary>Secret scope (secrets.scope).</summary>
public enum SecretScope
{
    Org,
    Project,
}

public static class SecretScopeExtensions
{
    public static string ToDbString(this SecretScope scope) => scope switch
    {
        SecretScope.Org => "org",
        SecretScope.Project => "project",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    public static SecretScope FromDbString(string value) => value switch
    {
        "org" => SecretScope.Org,
        "project" => SecretScope.Project,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown secret scope"),
    };
}

/// <summary>User role for RBAC (users.role).</summary>
public enum UserRole
{
    Owner,
    Maintainer,
    Developer,
    Viewer,
}

public static class UserRoleExtensions
{
    public static string ToDbString(this UserRole role) => role switch
    {
        UserRole.Owner => "owner",
        UserRole.Maintainer => "maintainer",
        UserRole.Developer => "developer",
        UserRole.Viewer => "viewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static UserRole FromDbString(string value) => value switch
    {
        "owner" => UserRole.Owner,
        "maintainer" => UserRole.Maintainer,
        "developer" => UserRole.Developer,
        "viewer" => UserRole.Viewer,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown user role"),
    };
}
