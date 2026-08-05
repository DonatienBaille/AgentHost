using AgentHost.Api.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentHost.Api.Services;

public interface IAgentManifestParser
{
    /// <summary>Parses the agent manifest YAML shape described in spec section 6.2.</summary>
    AgentManifest Parse(string yaml);
}

public class AgentManifestParser : IAgentManifestParser
{
    private readonly IDeserializer _deserializer;

    public AgentManifestParser()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public AgentManifest Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ArgumentException("Manifest YAML is empty", nameof(yaml));

        RawManifest raw;
        try
        {
            raw = _deserializer.Deserialize<RawManifest>(yaml)
                ?? throw new InvalidOperationException("Manifest YAML deserialized to null");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse agent manifest YAML: {ex.Message}", ex);
        }

        if (raw.Spec is null)
            throw new InvalidOperationException("Agent manifest is missing required 'spec' section");

        var manifest = new AgentManifest
        {
            ApiVersion = raw.ApiVersion ?? "agenthost.dev/v1",
            Kind = raw.Kind ?? "Agent",
            Metadata = new AgentManifestMetadata
            {
                Name = raw.Metadata?.Name ?? string.Empty,
                DisplayName = raw.Metadata?.DisplayName ?? raw.Metadata?.Name ?? string.Empty,
                Description = raw.Metadata?.Description ?? string.Empty,
            },
            Spec = new AgentManifestSpec
            {
                Type = raw.Spec.Type ?? "oci",
                Image = raw.Spec.Image,
                External = raw.Spec.External is null
                    ? null
                    : new AgentManifestExternal
                    {
                        Provider = raw.Spec.External.Provider ?? string.Empty,
                        Model = raw.Spec.External.Model,
                        Config = raw.Spec.External.Config ?? new Dictionary<string, string>(),
                    },
                Inputs = raw.Spec.Inputs ?? new Dictionary<string, object?>(),
                Outputs = raw.Spec.Outputs,
                Permissions = new AgentManifestPermissions
                {
                    Vcs = raw.Spec.Permissions?.Vcs ?? "none",
                    Network = raw.Spec.Permissions?.Network ?? "none",
                    Secrets = raw.Spec.Permissions?.Secrets ?? new List<string>(),
                    Docker = raw.Spec.Permissions?.Docker ?? false,
                },
                Runtime = new AgentManifestRuntime
                {
                    Profile = raw.Spec.Runtime?.Profile ?? "standard",
                    Cpu = raw.Spec.Runtime?.Cpu ?? 2,
                    Memory = raw.Spec.Runtime?.Memory ?? "2Gi",
                    Disk = raw.Spec.Runtime?.Disk ?? "10Gi",
                    MaxDurationSeconds = raw.Spec.Runtime?.MaxDurationSeconds ?? 3600,
                },
                Budget = new AgentManifestBudget
                {
                    DefaultMaxUsd = raw.Spec.Budget?.DefaultMaxUsd ?? 5.0m,
                    HardMaxUsd = raw.Spec.Budget?.HardMaxUsd ?? 10.0m,
                },
                Approvals = raw.Spec.Approvals?.BeforeWrite is null
                    ? null
                    : new AgentManifestApprovals
                    {
                        BeforeWrite = new AgentManifestApprovalGate
                        {
                            RequiredRole = raw.Spec.Approvals.BeforeWrite.RequiredRole ?? "maintainer",
                            RequiredCount = raw.Spec.Approvals.BeforeWrite.RequiredCount ?? 1,
                        },
                    },
            },
        };

        if (string.IsNullOrWhiteSpace(manifest.Metadata.Name))
            throw new InvalidOperationException("Agent manifest is missing required 'metadata.name'");

        return manifest;
    }

    // Loosely-typed mirror of the YAML shape; every field is optional at this stage so that
    // partial/legacy manifests still parse, with validation/defaulting applied above.
    private class RawManifest
    {
        public string? ApiVersion { get; set; }
        public string? Kind { get; set; }
        public RawMetadata? Metadata { get; set; }
        public RawSpec? Spec { get; set; }
    }

    private class RawMetadata
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
    }

    private class RawSpec
    {
        public string? Type { get; set; }
        public string? Image { get; set; }
        public RawExternal? External { get; set; }
        public Dictionary<string, object?>? Inputs { get; set; }
        public Dictionary<string, object?>? Outputs { get; set; }
        public RawPermissions? Permissions { get; set; }
        public RawRuntime? Runtime { get; set; }
        public RawBudget? Budget { get; set; }
        public RawApprovals? Approvals { get; set; }
    }

    private class RawExternal
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public Dictionary<string, string>? Config { get; set; }
    }

    private class RawPermissions
    {
        public string? Vcs { get; set; }
        public string? Network { get; set; }
        public List<string>? Secrets { get; set; }
        public bool? Docker { get; set; }
    }

    private class RawRuntime
    {
        public string? Profile { get; set; }
        public long? Cpu { get; set; }
        public string? Memory { get; set; }
        public string? Disk { get; set; }
        public int? MaxDurationSeconds { get; set; }
    }

    private class RawBudget
    {
        public decimal? DefaultMaxUsd { get; set; }
        public decimal? HardMaxUsd { get; set; }
    }

    private class RawApprovals
    {
        public RawApprovalGate? BeforeWrite { get; set; }
    }

    private class RawApprovalGate
    {
        public string? RequiredRole { get; set; }
        public int? RequiredCount { get; set; }
    }
}
