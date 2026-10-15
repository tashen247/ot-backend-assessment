using Microsoft.Extensions.Options;
using OT.Assessment.Core.Abstractions;
using RabbitMQ.Client;

namespace OT.Assessment.Infrastructure.Messaging;

/// <summary>
/// Publishes poison messages to <c>ot.casino.wagers.dead</c> and waits for the broker confirm.
/// The consumer acks the original delivery only after this returns.
/// </summary>
public sealed class RabbitMqDeadLetterPublisher : IDeadLetterPublisher, IAsyncDisposable
{
    private static readonly CreateChannelOptions ConfirmedChannel =
        new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel _channel;

    public RabbitMqDeadLetterPublisher(RabbitMqConnectionProvider connectionProvider, IOptions<RabbitMqOptions> options)
    {
        _connectionProvider = connectionProvider;
        _options = options.Value;
    }

    public async Task PublishConfirmedAsync(ReadOnlyMemory<byte> body, string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is null || _channel.IsClosed)
            {
                _channel?.Dispose();
                _channel = null;
                var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
                _channel = await connection.CreateChannelAsync(ConfirmedChannel, cancellationToken);
                await RabbitMqTopology.DeclareAsync(_channel, _options, cancellationToken);
            }

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                Headers = new Dictionary<string, object> { ["dead-letter-reason"] = reason ?? "rejected" }
            };

            await _channel.BasicPublishAsync(
                _options.DeadLetterExchange,
                _options.RoutingKey,
                mandatory: true,
                properties,
                body,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync();
            _channel.Dispose();
        }

        _gate.Dispose();
    }
}
