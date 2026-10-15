namespace OT.Assessment.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    public string Exchange { get; set; } = "ot.casino";
    public string RoutingKey { get; set; } = "casino.wager";
    public string Queue { get; set; } = "ot.casino.wagers";

    /// <summary>Messages that cannot be processed are routed here instead of being lost or retried forever.</summary>
    public string DeadLetterExchange { get; set; } = "ot.casino.dlx";
    public string DeadLetterQueue { get; set; } = "ot.casino.wagers.dead";

    /// <summary>
    /// Number of channels the API publishes on concurrently. Each channel waits for broker
    /// confirms independently, so this bounds in-flight publishes.
    /// </summary>
    public int PublisherChannelPoolSize { get; set; } = 16;
}
