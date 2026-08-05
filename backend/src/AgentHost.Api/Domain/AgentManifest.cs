namespace AgentHost.Api.Domain;

/// <summary>
/// Strongly-typed model of the agent manifest YAML shape described in spec section 6.2.
/// Produced by <see cref="AgentHost.Api.Services.IAgentManifestParser"/>.
/// </summary>
public class AgentManifest
{
    public string ApiVersion { get; set; } = "agenthost.dev/v1";
    public string Kind { get; set; } = "Agent";
    public AgentManifestMetadata Metadata { get; set; } = new();
    public AgentManifestSpec Spec { get; set; } = new();
}

public class AgentManifestMetadata
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class AgentManifestSpec
{
    public string Type { get; set; } = "oci"; // oci, copilot, claude_code, openai, custom
    public string? Image { get; set; }
    public AgentManifestExternal? External { get; set; }

    /// <summary>Raw JSON Schema for inputs, kept as a dictionary tree (parsed from YAML).</summary>
    public Dictionary<string, object?> Inputs { get; set; } = new();

    /// <summary>Raw JSON Schema for outputs.</summary>
    public Dictionary<string, object?>? Outputs { get; set; }

    public AgentManifestPermissions Permissions { get; set; } = new();
    public AgentManifestRuntime Runtime { get; set; } = new();
    public AgentManifestBudget Budget { get; set; } = new();
    public AgentManifestApprovals? Approvals { get; set; }
}

public class AgentManifestExternal
{
    public string Provider { get; set; } = string.Empty; // github_copilot, claude_code, openai, ...
    public string? Model { get; set; }
    public Dictionary<string, string> Config { get; set; } = new();
}

public class AgentManifestPermissions
{
    public string Vcs { get; set; } = "none"; // none | read | write_branch | write_pr | push_default
    public string Network { get; set; } = "none"; // none | allowlist | full
    public List<string> Secrets { get; set; } = new();
    public bool Docker { get; set; }
}

public class AgentManifestRuntime
{
    public string Profile { get; set; } = "standard";
    public long Cpu { get; set; } = 2;
    public string Memory { get; set; } = "2Gi";
    public string Disk { get; set; } = "10Gi";
    public int MaxDurationSeconds { get; set; } = 3600;
}

public class AgentManifestBudget
{
    public decimal DefaultMaxUsd { get; set; } = 5.0m;
    public decimal HardMaxUsd { get; set; } = 10.0m;
}

public class AgentManifestApprovals
{
    public AgentManifestApprovalGate? BeforeWrite { get; set; }
}

public class AgentManifestApprovalGate
{
    public string RequiredRole { get; set; } = "maintainer";
    public int RequiredCount { get; set; } = 1;
}
