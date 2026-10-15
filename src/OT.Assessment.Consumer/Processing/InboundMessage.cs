namespace OT.Assessment.Consumer.Processing;

/// <summary>A delivery copied out of the RabbitMQ callback (the body buffer is only valid inside it).</summary>
internal readonly record struct InboundMessage(ulong DeliveryTag, ReadOnlyMemory<byte> Body);
