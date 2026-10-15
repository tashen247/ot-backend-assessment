using OT.Assessment.Core.Abstractions;
using RabbitMQ.Client;

namespace OT.Assessment.Consumer.Processing;

/// <summary>Acks and requeues individual delivery tags. A multiple-ack is never used.</summary>
internal interface IDeliveryAcker
{
    Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken);

    Task RequeueAsync(ulong deliveryTag, CancellationToken cancellationToken);
}

internal sealed class ChannelDeliveryAcker : IDeliveryAcker
{
    private readonly IChannel _channel;

    public ChannelDeliveryAcker(IChannel channel) => _channel = channel;

    public Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken) =>
        _channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).AsTask();

    public Task RequeueAsync(ulong deliveryTag, CancellationToken cancellationToken) =>
        _channel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken).AsTask();
}

/// <summary>
/// Settles exactly the tags in the batch result, one tag at a time. With a single channel and an
/// ordered buffer a batch is always a contiguous run of tags, so a multiple-ack would also be
/// correct today; per-tag settlement keeps that correct without relying on the invariant (for
/// example if batches were ever processed in parallel). A poison message is published to the
/// dead-letter queue first; its original delivery is acked only after that publish returns.
/// </summary>
internal sealed class DeliverySettler
{
    private readonly IDeadLetterPublisher _deadLetters;

    public DeliverySettler(IDeadLetterPublisher deadLetters) => _deadLetters = deadLetters;

    public async Task SettleAsync(
        IReadOnlyList<InboundMessage> batch,
        BatchResult result,
        IDeliveryAcker acker,
        CancellationToken cancellationToken)
    {
        var dead = result.DeadLettered.ToHashSet();
        var bodies = batch.ToDictionary(m => m.DeliveryTag, m => m.Body);

        foreach (var tag in result.DeadLettered)
        {
            if (!bodies.TryGetValue(tag, out var body))
            {
                continue;
            }

            await _deadLetters.PublishConfirmedAsync(body, result.ReasonFor(tag), cancellationToken);
            await acker.AckAsync(tag, cancellationToken);
        }

        foreach (var tag in result.Acknowledged)
        {
            if (dead.Contains(tag))
            {
                continue;
            }

            await acker.AckAsync(tag, cancellationToken);
        }
    }

    public async Task RequeueAsync(IReadOnlyList<InboundMessage> batch, IDeliveryAcker acker, CancellationToken cancellationToken)
    {
        foreach (var message in batch)
        {
            await acker.RequeueAsync(message.DeliveryTag, cancellationToken);
        }
    }
}
