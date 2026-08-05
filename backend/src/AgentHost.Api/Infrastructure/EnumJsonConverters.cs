using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// JSON converters that serialize enums using the same lowercase/snake_case strings as the
/// database (each enum's ToDbString()/FromDbString()) instead of System.Text.Json's default
/// PascalCase member names — this is the wire format documented for API consumers (e.g.
/// RunStatus "awaiting_approval", not "AwaitingApproval") and what the frontend expects.
/// </summary>
public class RunStatusJsonConverter : JsonConverter<RunStatus>
{
    public override RunStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => RunStatusExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, RunStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}

public class AgentTypeJsonConverter : JsonConverter<AgentType>
{
    public override AgentType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => AgentTypeExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, AgentType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}

public class TriggeredByTypeJsonConverter : JsonConverter<TriggeredByType>
{
    public override TriggeredByType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TriggeredByTypeExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, TriggeredByType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}

public class ApprovalTypeJsonConverter : JsonConverter<ApprovalType>
{
    public override ApprovalType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ApprovalTypeExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ApprovalType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}

public class ApprovalStatusJsonConverter : JsonConverter<ApprovalStatus>
{
    public override ApprovalStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ApprovalStatusExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ApprovalStatus value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}

public class SecretScopeJsonConverter : JsonConverter<SecretScope>
{
    public override SecretScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => SecretScopeExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SecretScope value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}

public class UserRoleJsonConverter : JsonConverter<UserRole>
{
    public override UserRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => UserRoleExtensions.FromDbString(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, UserRole value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToDbString());
}
