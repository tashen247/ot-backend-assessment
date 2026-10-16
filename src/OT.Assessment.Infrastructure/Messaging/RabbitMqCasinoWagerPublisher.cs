using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;
using OT.Assessment.Infrastructure.Resilience;
using Polly;
using Polly.CircuitBreaker;
using RabbitMQ.Client;
using IAmqpChannel = RabbitMQ.Client.IChannel;

namespace OT.Assessment.Infrastructure.Messaging;

/// <summary>
/// Publishes wagers with publisher confirms: <see cref="PublishAsync"/> completes only after the
/// broker has persisted the message, so a 200 from the API means the wager will not be lost.
/// <para>
/// AMQP channels should not be shared across concurrent publishers, and opening one per request
/// is expensive, so a fixed pool of channels is shared by all requests. A slot holds a channel
/// that is created lazily and replaced if the broker closes it or a publish on it fails.
/// </para>
/// <para>
/// Each publish is bounded by a timeout and runs behind a circuit breaker. When publishes keep
/// failing, or the broker reports that it is blocking publishers, the circuit opens and requests
/// are refused at once, before a channel is rented, instead of each one waiting to fail. The
/// caller is told how long to wait before retrying.
/// </para>
/// </summary>
public sealed class RabbitMqCasinoWagerPublisher : ICasinoWagerPublisher, IAsyncDisposable
{
    private static readonly CreateChannelOptions ConfirmedChannel =
        new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqCasinoWagerPublisher> _logger;
    private readonly Channel<IAmqpChannel> _pool;
    private readonly int _poolSize;
    private readonly SemaphoreSlim _topologyLock = new(1, 1);
    private readonly ResiliencePipeline _pipeline;
    private volatile bool _topologyDeclared;
    private IConnection _watchedConnection;
    private int _isolatedByBroker;

    public RabbitMqCasinoWagerPublisher(
        RabbitMqConnectionProvider connectionProvider,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqCasinoWagerPublisher> logger,
        TimeProvider timeProvider = null)
    {
        _connectionProvider = connectionProvider;
        _options = options.Value;
        _logger = logger;
        var time = timeProvider ?? TimeProvider.System;

        _poolSize = Math.Max(1, _options.PublisherChannelPoolSize);
        _pool = Channel.CreateBounded<IAmqpChannel>(_poolSize);
        for (var i = 0; i < _poolSize; i++)
        {
            _pool.Writer.TryWrite(null); // empty slot, filled on first use
        }

        Circuit = new CircuitBreakerTracker("RabbitMQ publish", _options.PublishCircuitBreaker, logger, time);

        // The breaker is outermost so that a publish that times out counts as one failure.
        // Caller cancellation (the HTTP request went away) says nothing about the broker and is not counted.
        _pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddCircuitBreaker(Circuit.CreateOptions(ex => ex is not OperationCanceledException))
            .AddTimeout(_options.PublishTimeout)
            .Build();
    }

    /// <summary>State of the publish circuit breaker, for health reporting.</summary>
    public CircuitBreakerTracker Circuit { get; }

    public async Task PublishAsync(CasinoWagerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Fail fast without touching the pool: an open circuit must not hold up the channels.
        if (Circuit.IsOpen)
        {
            throw Unavailable(message, null);
        }

        var lease = new ChannelLease { Channel = await _pool.Reader.ReadAsync(cancellationToken) };
        try
        {
            await _pipeline.ExecuteAsync(
                static (state, token) => state.Self.PublishOnAsync(state.Lease, state.Message, token),
                (Self: this, Lease: lease, Message: message),
                cancellationToken);
        }
        catch (BrokenCircuitException ex)
        {
            // Rejected before the channel was used, so it goes back to the pool as it is.
            throw Unavailable(message, ex);
        }
        catch (Exception ex)
        {
            // A publish that failed or was abandoned part-way may leave the channel waiting on a
            // confirm that will never be read, so the slot is emptied rather than reused.
            DiscardChannel(lease);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new MessagePublishException($"Wager {message.WagerId} could not be published to RabbitMQ.", ex);
        }
        finally
        {
            _pool.Writer.TryWrite(lease.Channel);
        }
    }

    private async ValueTask PublishOnAsync(ChannelLease lease, CasinoWagerMessage message, CancellationToken cancellationToken)
    {
        if (lease.Channel is null || lease.Channel.IsClosed)
        {
            DiscardChannel(lease); // if opening fails, the slot goes back empty rather than holding a dead channel
            lease.Channel = await OpenChannelAsync(cancellationToken);
        }

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = message.WagerId.ToString(),
            Type = nameof(CasinoWagerMessage),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };

        // WaitAsync stops waiting as soon as the token is cancelled (by the publish timeout or the caller),
        // even if the client does not. A broker that is blocking publishers leaves the client's write
        // stuck regardless of the token, which in testing held a request for 25 s against a 5 s
        // timeout. The abandoned publish stays on this channel, which is then discarded.
        await lease.Channel.BasicPublishAsync(_options.Exchange, _options.RoutingKey, mandatory: true,
                properties, MessageSerializer.Serialize(message), cancellationToken)
            .AsTask()
            .WaitAsync(cancellationToken);
    }

    private MessagePublishException Unavailable(CasinoWagerMessage message, Exception inner) =>
        new($"Wager {message.WagerId} was not published: RabbitMQ is unavailable (publish circuit {Circuit.State}).",
            inner ?? new BrokenCircuitException(), Circuit.RetryAfter);

    /// <summary>
    /// Empties the slot at once and closes the old channel in the background. Closing waits for the
    /// broker to acknowledge, and a broker that is blocking publishers does not, so closing inline
    /// held the request for about 20 s after the publish had already timed out.
    /// </summary>
    private static void DiscardChannel(ChannelLease lease)
    {
        var channel = lease.Channel;
        lease.Channel = null;

        if (channel is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                    // Best effort: the channel is being thrown away because it is already broken.
                }
            });
        }
    }

    /// <summary>
    /// Opens the connection, declares the topology and fills every pool slot, so the first burst
    /// of requests after start-up does not pay for it.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var rented = new List<IAmqpChannel>();
        try
        {
            while (rented.Count < _poolSize)
            {
                rented.Add(await _pool.Reader.ReadAsync(cancellationToken));
            }

            for (var i = 0; i < rented.Count; i++)
            {
                if (rented[i] is null || rented[i].IsClosed)
                {
                    rented[i] = await OpenChannelAsync(cancellationToken);
                }
            }
        }
        finally
        {
            foreach (var channel in rented)
            {
                _pool.Writer.TryWrite(channel);
            }
        }
    }

    private async Task<IAmqpChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(cancellationToken);
        WatchForBlocking(connection);

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

    /// <summary>
    /// Under a memory or disk alarm the broker keeps the connection open but stops reading from
    /// publishers, and says so. Opening the circuit on that signal refuses wagers immediately instead
    /// of letting requests time out one by one; it closes again when the broker unblocks, or when
    /// the blocked connection is lost or recovered.
    /// </summary>
    private void WatchForBlocking(IConnection connection)
    {
        if (Interlocked.CompareExchange(ref _watchedConnection, connection, null) is not null)
        {
            return;
        }

        connection.ConnectionBlockedAsync += async (_, args) =>
        {
            _logger.LogWarning("RabbitMQ is blocking publishers: {Reason}", args.Reason);
            Interlocked.Exchange(ref _isolatedByBroker, 1);
            await Circuit.ManualControl.IsolateAsync();
        };

        connection.ConnectionUnblockedAsync += (_, _) => EndBrokerIsolationAsync("RabbitMQ has stopped blocking publishers");

        // "Unblocked" is never sent if the blocked connection is lost instead, for example when the
        // alarm ends with the broker restarting. Without these, the circuit would stay isolated and
        // refuse every wager after the broker was healthy again. Once the connection is gone the
        // isolation no longer describes anything; publishes that fail from here on trip the circuit
        // in the ordinary way.
        connection.ConnectionShutdownAsync += (_, _) => EndBrokerIsolationAsync("RabbitMQ connection closed while publishers were blocked");
        connection.RecoverySucceededAsync += (_, _) => EndBrokerIsolationAsync("RabbitMQ connection recovered while publishers were blocked");
    }

    /// <summary>
    /// Closes the circuit only if it was isolated because the broker blocked publishers; a circuit
    /// opened by failed publishes is left to its own break.
    /// </summary>
    private async Task EndBrokerIsolationAsync(string reason)
    {
        if (Interlocked.Exchange(ref _isolatedByBroker, 0) == 1)
        {
            _logger.LogInformation("{Reason}; publishing resumes", reason);
            await Circuit.ManualControl.CloseAsync();
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

    /// <summary>The channel rented for one publish; replaced in place if it has to be reopened.</summary>
    private sealed class ChannelLease
    {
        public IAmqpChannel Channel { get; set; }
    }
}
