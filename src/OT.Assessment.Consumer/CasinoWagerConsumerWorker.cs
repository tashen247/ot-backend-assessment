using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OT.Assessment.Consumer.Processing;
using OT.Assessment.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OT.Assessment.Consumer;

/// <summary>
/// Consumes wager messages and stores them in batches.
/// <para>
/// The RabbitMQ callback only copies each delivery into an in-memory buffer; a single loop drains
/// the buffer in batches, writes each batch in one stored procedure call and then acknowledges each
/// delivery on its own tag. Messages are acknowledged only after the database commit, so a crash at
/// any point leads to redelivery, never loss; the stored procedure ignores wagers it has already
/// stored, so redelivery never double-counts. A poison message is published to the dead-letter
/// queue and acked only after the broker confirms that publish.
/// </para>
/// </summary>
internal sealed class CasinoWagerConsumerWorker : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMqConnectionProvider _connectionProvider;
    private readonly CasinoWagerBatchProcessor _processor;
    private readonly DeliverySettler _settler;
    private readonly RabbitMqOptions _rabbit;
    private readonly ConsumerOptions _options;
    private readonly ILogger<CasinoWagerConsumerWorker> _logger;
    private long _totalInserted;

    public CasinoWagerConsumerWorker(
        RabbitMqConnectionProvider connectionProvider,
        CasinoWagerBatchProcessor processor,
        DeliverySettler settler,
        IOptions<RabbitMqOptions> rabbit,
        IOptions<ConsumerOptions> options,
        ILogger<CasinoWagerConsumerWorker> logger)
    {
        _connectionProvider = connectionProvider;
        _processor = processor;
        _settler = settler;
        _rabbit = rabbit.Value;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consumer stopped unexpectedly; restarting in {Delay}", RestartDelay);
                await Task.Delay(RestartDelay, stoppingToken);
            }
        }

        _logger.LogInformation("Consumer stopped. {Total} wagers stored this run", _totalInserted);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await RabbitMqTopology.DeclareAsync(channel, _rabbit, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: (ushort)_options.PrefetchCount, global: false, stoppingToken);

        // Bounded at the prefetch count, so it can never block the broker callback for long.
        var buffer = System.Threading.Channels.Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(_options.PrefetchCount)
        {
            SingleReader = true,
            SingleWriter = true
        });

        channel.ChannelShutdownAsync += (_, args) =>
        {
            buffer.Writer.TryComplete(new InvalidOperationException($"Channel closed: {args.ReplyText}"));
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
            buffer.Writer.WriteAsync(new InboundMessage(delivery.DeliveryTag, delivery.Body.ToArray()), stoppingToken).AsTask();

        await channel.BasicConsumeAsync(_rabbit.Queue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("Consuming from {Queue} (batch size {BatchSize}, prefetch {Prefetch})",
            _rabbit.Queue, _options.BatchSize, _options.PrefetchCount);

        while (true)
        {
            var batch = await BatchReader.ReadBatchAsync(buffer.Reader, _options.BatchSize, _options.BatchWindow, stoppingToken);
            await HandleBatchAsync(new ChannelDeliveryAcker(channel), batch, stoppingToken);
        }
    }

    private async Task HandleBatchAsync(IDeliveryAcker acker, List<InboundMessage> batch, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        BatchResult result;

        try
        {
            // Not tied to the stopping token: a batch that has started is allowed to finish on shutdown.
            result = await _processor.ProcessAsync(batch, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch of {Count} wagers could not be stored; requeueing", batch.Count);
            await _settler.RequeueAsync(batch, acker, CancellationToken.None);
            await Task.Delay(_options.RequeueDelayMilliseconds, stoppingToken);
            return;
        }

        await _settler.SettleAsync(batch, result, acker, CancellationToken.None);

        _totalInserted += result.Inserted;
        _logger.LogInformation(
            "Batch of {Count}: {Inserted} stored, {Duplicates} already stored, {DeadLettered} dead-lettered in {ElapsedMs} ms ({Total} total)",
            batch.Count, result.Inserted, result.Acknowledged.Count - result.Inserted, result.DeadLettered.Count,
            stopwatch.ElapsedMilliseconds, _totalInserted);
    }
}
