using System.Text.Json.Nodes;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

/// <summary>Body of <c>POST /api/agents/validate-manifest</c>.</summary>
public class ValidateManifestRequest
{
    /// <summary>Manifest YAML exactly as typed by the user — never trimmed or rewritten server-side.</summary>
    public string ManifestYaml { get; set; } = string.Empty;
}

/// <summary>
/// Result of a dry-run parse. The endpoint answers <c>200 OK</c> for both outcomes: a manifest that
/// does not parse is a normal, expected answer to "is this valid yet?" while the user is typing,
/// not a failed request. Callers branch on <see cref="Valid"/>.
/// </summary>
public class ValidateManifestResponse
{
    public bool Valid { get; set; }

    /// <summary>The manifest as <see cref="Services.IAgentManifestParser"/> normalizes it (defaults applied). Null when invalid.</summary>
    public AgentManifest? Manifest { get; set; }

    /// <summary>The sandbox knobs that live in <c>spec.permissions</c> but not in <see cref="AgentManifest"/>.</summary>
    public ManifestPermissionExtensions? PermissionExtensions { get; set; }

    /// <summary>
    /// The whole YAML document projected to JSON, scalars included, with no key dropped.
    ///
    /// The typed parser is deliberately lenient (<c>IgnoreUnmatchedProperties</c>) and coerces every
    /// free-form scalar to a string, so it cannot answer "what does this manifest contain that the
    /// UI form does not cover?". The UI needs that answer to warn about — and preserve — the parts
    /// of a manifest its form cannot edit, so the raw document travels alongside the typed one.
    /// </summary>
    public JsonNode? Document { get; set; }

    /// <summary>Set when <see cref="Valid"/> is false.</summary>
    public ManifestValidationError? Error { get; set; }
}

/// <summary>Mirrors <see cref="Services.AgentContainerPolicy"/> on the wire.</summary>
public class ManifestPermissionExtensions
{
    public List<string> NetworkAllowlist { get; set; } = new();
    public bool WritableRootfs { get; set; }
}

/// <summary>
/// A parse failure, located when the underlying YAML reader knows where it happened.
/// <see cref="Line"/> and <see cref="Column"/> are 1-based and null for errors that are not tied to
/// a position (an empty document, a missing required section).
/// </summary>
public class ManifestValidationError
{
    public string Message { get; set; } = string.Empty;
    public int? Line { get; set; }
    public int? Column { get; set; }
}
