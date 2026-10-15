namespace OT.Assessment.Consumer.Processing;

/// <summary>How each delivery in a batch should be settled with the broker.</summary>
internal sealed class BatchResult
{
    private readonly Dictionary<ulong, string> _reasons = [];

    public List<ulong> Acknowledged { get; } = [];

    /// <summary>Deliveries that can never succeed (malformed or rejected by the database); published to the dead-letter queue and then acked.</summary>
    public List<ulong> DeadLettered { get; } = [];

    public int Inserted { get; set; }

    public void DeadLetter(ulong deliveryTag, string reason)
    {
        DeadLettered.Add(deliveryTag);
        _reasons[deliveryTag] = reason;
    }

    public string ReasonFor(ulong deliveryTag) =>
        _reasons.TryGetValue(deliveryTag, out var reason) ? reason : "rejected";
}
