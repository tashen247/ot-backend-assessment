using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OT.Assessment.Infrastructure.Messaging;

/// <summary>
/// Warms the publisher's connection and channel pool at start-up. A broker that is down at
/// start-up is logged, not fatal: the API still starts, /health/ready reports it, and the
/// publisher connects lazily once the broker is back.
/// </summary>
internal sealed class RabbitMqPublisherWarmupService : IHostedService
{
    private readonly RabbitMqCasinoWagerPublisher _publisher;
    private readonly ILogger<RabbitMqPublisherWarmupService> _logger;

    public RabbitMqPublisherWarmupService(RabbitMqCasinoWagerPublisher publisher, ILogger<RabbitMqPublisherWarmupService> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.WarmUpAsync(cancellationToken);
            _logger.LogInformation("RabbitMQ publisher warmed up");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RabbitMQ is not reachable at start-up; the publisher will connect on first use");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
