using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;
using RabbitMQ.Client;
using IAmqpChannel = RabbitMQ.Client.IChannel;

namespace OT.Assessment.Infrastructure.Messaging;

/// <summary>
/// Publishes wagers with publisher confirms: <see cref="PublishAsync"/> completes only after the
/// broker has persisted the message, so a 200 from the API means the wager will not be lost.
/// <para>
/// AMQP channels should not be shared across concurrent publishers, and opening one per request
/// is expensive, so a fixed pool of channels is shared by all requests. A slot holds a channel
/// that is created lazily and replaced if the broker closes it.
/// </para>
/// </summary>
public sealed class RabbitMqCasinoWagerPublisher : ICasinoWagerPublisher, IAsyncDisposable
{
    private static readonly CreateChannelOptions ConfirmedChannel =
        new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqOptions _options;
    private readonly Channel<IAmqpChannel> _pool;
    private readonly SemaphoreSlim _topologyLock = new(1, 1);
    private volatile bool _topologyDeclared;

    public RabbitMqCasinoWagerPublisher(RabbitMqConnectionProvider connectionProvider, IOptions<RabbitMqOptions> options)
    {
        _connectionProvider = connectionProvider;
        _options = options.Value;

        var poolSize = Math.Max(1, _options.PublisherChannelPoolSize);
        _pool = Channel.CreateBounded<IAmqpChannel>(poolSize);
        for (var i = 0; i < poolSize; i++)
        {
            _pool.Writer.TryWrite(null); // empty slot, filled on first use
        }
    }

    public async Task PublishAsync(CasinoWagerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var channel = await _pool.Reader.ReadAsync(cancellationToken);
        try
        {
            if (channel is null || channel.IsClosed)
            {
                channel?.Dispose();
                channel = null; // if opening fails, the slot goes back empty rather than holding a dead channel
                channel = await OpenChannelAsync(cancellationToken);
            }

            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.WagerId.ToString(),
                Type = nameof(CasinoWagerMessage),
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            };

            await channel.BasicPublishAsync(_options.Exchange, _options.RoutingKey, mandatory: true,
                properties, MessageSerializer.Serialize(message), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MessagePublishException($"Wager {message.WagerId} could not be published to RabbitMQ.", ex);
        }
        finally
        {
            _pool.Writer.TryWrite(channel);
        }
    }

    private async Task<IAmqpChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(ConfirmedChannel, cancellationToken);

        if (_topologyDeclared)
        {
            return channel;
        }

        await _topologyLock.WaitAsync(cancellationToken);
        try
        {
            if (!_topologyDeclared)
            {
                await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken);
                _topologyDeclared = true;
            }

            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
        finally
        {
            _topologyLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pool.Writer.TryComplete();
        while (_pool.Reader.TryRead(out var channel))
        {
            if (channel is not null)
            {
                await channel.CloseAsync();
                channel.Dispose();
            }
        }

        _topologyLock.Dispose();
    }
}
