using Microsoft.Data.SqlClient;

namespace OT.Assessment.Infrastructure.Persistence;

public interface ISqlConnectionFactory
{
    /// <summary>Creates an unopened connection. Connection pooling is handled by SqlClient.</summary>
    SqlConnection Create();
}

internal sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public SqlConnection Create() => new(_connectionString);
}
