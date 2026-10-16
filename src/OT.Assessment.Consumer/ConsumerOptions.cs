using OT.Assessment.Infrastructure.Resilience;

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

    /// <summary>
    /// Pause before trying again after a batch could not be stored, so a database outage does not
    /// become a hot loop. While the circuit is open the wait is the time left on the break instead.
    /// </summary>
    public int RequeueDelayMilliseconds { get; set; } = 5000;

    /// <summary>
    /// Opens after SQL Server has failed with transient errors (outlasting the retries) for every
    /// batch in the window. While open, the consumer holds its current batch unacknowledged and
    /// stops reading new deliveries, instead of cycling messages through nack and requeue.
    /// </summary>
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new()
    {
        FailureRatio = 1.0,
        MinimumThroughput = 2,
        SamplingDurationSeconds = 60,
        BreakDurationSeconds = 30
    };

    public TimeSpan BatchWindow => TimeSpan.FromMilliseconds(BatchWindowMilliseconds);

    public bool IsValid() =>
        BatchSize > 0 && BatchWindowMilliseconds > 0 && PrefetchCount >= BatchSize && PrefetchCount <= ushort.MaxValue
        && MaxWriteRetries >= 0 && RetryBaseDelayMilliseconds >= 0 && RequeueDelayMilliseconds >= 0
        && CircuitBreaker is not null && CircuitBreaker.IsValid();
}
