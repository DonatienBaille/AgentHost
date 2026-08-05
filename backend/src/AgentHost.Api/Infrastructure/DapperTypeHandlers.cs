using System.Data;
using System.Text.Json;
using Dapper;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Generic Dapper type handler that (de)serializes a CLR type to/from a Postgres jsonb column
/// via System.Text.Json. Values are passed as plain strings; SQL statements are responsible
/// for casting the parameter with `::jsonb` (Npgsql maps jsonb columns to `string` by default,
/// which is what this handler's Parse() receives on read).
/// </summary>
public class JsonTypeHandler<T> : SqlMapper.TypeHandler<T>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public override T? Parse(object value)
    {
        if (value is null || value is DBNull) return default;
        var json = value as string;
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public override void SetValue(IDbDataParameter parameter, T? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value, Options);
    }
}

/// <summary>Dapper handler mapping RunStatus &lt;-&gt; the lowercase/snake-case DB string.</summary>
public class RunStatusTypeHandler : SqlMapper.TypeHandler<RunStatus>
{
    public override RunStatus Parse(object value) => RunStatusExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, RunStatus value) => parameter.Value = value.ToDbString();
}

public class AgentTypeTypeHandler : SqlMapper.TypeHandler<AgentType>
{
    public override AgentType Parse(object value) => AgentTypeExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, AgentType value) => parameter.Value = value.ToDbString();
}

public class TriggeredByTypeTypeHandler : SqlMapper.TypeHandler<TriggeredByType>
{
    public override TriggeredByType Parse(object value) => TriggeredByTypeExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, TriggeredByType value) => parameter.Value = value.ToDbString();
}

public class ApprovalTypeTypeHandler : SqlMapper.TypeHandler<ApprovalType>
{
    public override ApprovalType Parse(object value) => ApprovalTypeExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, ApprovalType value) => parameter.Value = value.ToDbString();
}

public class ApprovalStatusTypeHandler : SqlMapper.TypeHandler<ApprovalStatus>
{
    public override ApprovalStatus Parse(object value) => ApprovalStatusExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, ApprovalStatus value) => parameter.Value = value.ToDbString();
}

public class SecretScopeTypeHandler : SqlMapper.TypeHandler<SecretScope>
{
    public override SecretScope Parse(object value) => SecretScopeExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, SecretScope value) => parameter.Value = value.ToDbString();
}

public class UserRoleTypeHandler : SqlMapper.TypeHandler<UserRole>
{
    public override UserRole Parse(object value) => UserRoleExtensions.FromDbString((string)value);
    public override void SetValue(IDbDataParameter parameter, UserRole value) => parameter.Value = value.ToDbString();
}

public static class DapperBootstrap
{
    public static void Configure()
    {
        // Match snake_case DB columns (org_id) to PascalCase CLR properties (OrgId) automatically.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        SqlMapper.AddTypeHandler(new RunStatusTypeHandler());
        SqlMapper.AddTypeHandler(new AgentTypeTypeHandler());
        SqlMapper.AddTypeHandler(new TriggeredByTypeTypeHandler());
        SqlMapper.AddTypeHandler(new ApprovalTypeTypeHandler());
        SqlMapper.AddTypeHandler(new ApprovalStatusTypeHandler());
        SqlMapper.AddTypeHandler(new SecretScopeTypeHandler());
        SqlMapper.AddTypeHandler(new UserRoleTypeHandler());

        SqlMapper.AddTypeHandler(new JsonTypeHandler<System.Text.Json.Nodes.JsonNode>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<RunHistoryItem>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<Pattern>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<Learning>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<Note>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<ArchivePeriod>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<ProjectContext>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<string>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<ApprovalResponse>>());
    }
}
