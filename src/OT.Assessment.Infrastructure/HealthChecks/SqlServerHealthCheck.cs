using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OT.Assessment.Infrastructure.Persistence;

namespace OT.Assessment.Infrastructure.HealthChecks;

internal sealed class SqlServerHealthCheck : IHealthCheck
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public SqlServerHealthCheck(ISqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.Create();
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "SQL Server is unreachable.", ex);
        }
    }
}
