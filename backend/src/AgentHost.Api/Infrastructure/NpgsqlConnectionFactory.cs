using System.Data;
using Npgsql;

namespace AgentHost.Api.Infrastructure;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

/// <summary>Creates Npgsql connections from `ConnectionStrings:DefaultConnection`.</summary>
public class NpgsqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured");
    }

    public IDbConnection CreateConnection()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
