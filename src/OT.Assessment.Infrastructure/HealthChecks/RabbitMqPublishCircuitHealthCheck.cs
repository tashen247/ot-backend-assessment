using Microsoft.Extensions.Diagnostics.HealthChecks;
using OT.Assessment.Infrastructure.Messaging;
using Polly.CircuitBreaker;

namespace OT.Assessment.Infrastructure.HealthChecks;

/// <summary>
/// Reports Degraded while the publish circuit is not closed: the API is up and still serves reads,
/// but it is refusing new wagers with 503 until RabbitMQ recovers.
/// </summary>
internal sealed class RabbitMqPublishCircuitHealthCheck : IHealthCheck
{
    private readonly RabbitMqCasinoWagerPublisher _publisher;

    public RabbitMqPublishCircuitHealthCheck(RabbitMqCasinoWagerPublisher publisher) => _publisher = publisher;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var circuit = _publisher.Circuit;
        var data = new Dictionary<string, object> { ["state"] = circuit.State.ToString() };

        var result = circuit.State switch
        {
            CircuitState.Closed => HealthCheckResult.Healthy("Publishing wagers.", data),
            CircuitState.HalfOpen => HealthCheckResult.Degraded("Publish circuit is half-open: testing whether RabbitMQ has recovered.", data: data),
            CircuitState.Isolated => HealthCheckResult.Degraded("Publish circuit is open: RabbitMQ is blocking publishers.", data: data),
            CircuitState.Open when !circuit.IsOpen => HealthCheckResult.Degraded("Publish circuit break is over: the next wager will test whether RabbitMQ has recovered.", data: data),
            _ => HealthCheckResult.Degraded(
                $"Publish circuit is open: new wagers are refused for another {Math.Ceiling(circuit.RetryAfter.TotalSeconds)} s.", data: data)
        };

        return Task.FromResult(result);
    }
}
