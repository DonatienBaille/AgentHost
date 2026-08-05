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

public static class DapperBootstrap
{
    public static void Configure()
    {
        // Match snake_case DB columns (org_id) to PascalCase CLR properties (OrgId) automatically.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // NOTE: do NOT add SqlMapper type handlers for the domain enums (RunStatus, UserRole, ...).
        // Dapper never consults them: writing, it short-circuits an enum parameter to its underlying
        // integral type before handler lookup (so the column silently gets '14' instead of
        // 'infra_error'); reading, it goes through Enum.Parse, which cannot produce
        // RunStatus.AwaitingApproval from 'awaiting_approval'. Seven such handlers existed here and
        // were dead code. Enum columns are instead mapped explicitly in Repositories/DbRows.cs,
        // which types them as string and converts via each enum's ToDbString()/FromDbString().

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
