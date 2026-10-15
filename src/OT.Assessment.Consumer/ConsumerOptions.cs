namespace OT.Assessment.Consumer;

public sealed class ConsumerOptions
{
    public const string SectionName = "Consumer";

    /// <summary>Maximum wagers written to SQL Server in one stored procedure call.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long to wait for a batch to fill before writing what has arrived.</summary>
    public int BatchWindowMilliseconds { get; set; } = 200;

    /// <summary>
    /// Unacknowledged messages the broker may push ahead of processing. Two batches' worth keeps
    /// the next batch filling while the current one is being written.
    /// </summary>
    public int PrefetchCount { get; set; } = 1000;

    /// <summary>Retries for transient SQL errors (deadlock, timeout, failover) before the batch is requeued.</summary>
    public int MaxWriteRetries { get; set; } = 5;

    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>Pause before resuming after a batch had to be requeued, so a database outage does not become a hot loop.</summary>
    public int RequeueDelayMilliseconds { get; set; } = 5000;

    public TimeSpan BatchWindow => TimeSpan.FromMilliseconds(BatchWindowMilliseconds);

    public bool IsValid() =>
        BatchSize > 0 && BatchWindowMilliseconds > 0 && PrefetchCount >= BatchSize && PrefetchCount <= ushort.MaxValue
        && MaxWriteRetries >= 0 && RetryBaseDelayMilliseconds >= 0 && RequeueDelayMilliseconds >= 0;
}
