namespace OT.Assessment.Consumer.Processing;

/// <summary>How each delivery in a batch should be settled with the broker.</summary>
internal sealed class BatchResult
{
    public List<ulong> Acknowledged { get; } = [];

    /// <summary>Deliveries that can never succeed (malformed or rejected by the database); sent to the dead-letter queue.</summary>
    public List<ulong> DeadLettered { get; } = [];

    public int Inserted { get; set; }
}
