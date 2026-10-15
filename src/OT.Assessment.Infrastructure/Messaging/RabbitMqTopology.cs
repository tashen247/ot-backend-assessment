using RabbitMQ.Client;

namespace OT.Assessment.Infrastructure.Messaging;

/// <summary>
/// Declares the exchanges and queues. Declarations are idempotent, so both the API and the
/// consumer call this at start-up and neither depends on the other having run first.
/// </summary>
public static class RabbitMqTopology
{
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Direct, durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(options.DeadLetterQueue, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(options.DeadLetterQueue, options.DeadLetterExchange, options.RoutingKey,
            cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Direct, durable: true,
            autoDelete: false, cancellationToken: cancellationToken);
        // Keep the dead-letter argument. RabbitMQ refuses to redeclare a queue whose arguments
        // differ, and the publisher turns that failure into HTTP 503. The consumer does not use
        // this argument: it publishes the poison message itself and acks the original only after
        // the broker confirms. The argument still covers a nack or expiry, which would otherwise drop.
        await channel.QueueDeclareAsync(options.Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = options.DeadLetterExchange
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(options.Queue, options.Exchange, options.RoutingKey,
            cancellationToken: cancellationToken);
    }
}
