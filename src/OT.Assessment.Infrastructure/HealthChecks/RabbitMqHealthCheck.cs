using Microsoft.Extensions.Diagnostics.HealthChecks;
using OT.Assessment.Infrastructure.Messaging;

namespace OT.Assessment.Infrastructure.HealthChecks;

internal sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnectionProvider _connectionProvider;

    public RabbitMqHealthCheck(RabbitMqConnectionProvider connectionProvider) => _connectionProvider = connectionProvider;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
            return connection.IsOpen
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection is closed.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ is unreachable.", ex);
        }
    }
}
