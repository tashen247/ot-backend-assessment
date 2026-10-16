using System.Text.Json;
using Microsoft.Extensions.Options;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;
using OT.Assessment.Infrastructure.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace OT.Assessment.Consumer.Processing;

/// <summary>
/// Turns a batch of deliveries into one database write and decides how each delivery is settled.
/// Has no RabbitMQ dependency, so the settlement rules are unit tested directly.
/// <list type="number">
/// <item>Malformed or invalid messages are dead-lettered; they would fail on every redelivery.</item>
/// <item>The valid remainder is written in one call, retrying transient SQL errors with backoff.</item>
/// <item>If the database rejects the batch outright, each message is written alone so that one bad row
/// is dead-lettered instead of blocking the other 499.</item>
/// <item>If transient errors outlast the retries, the exception propagates and the caller holds the batch and tries again later.</item>
/// <item>A circuit breaker sits outside the retries. When batch after batch fails with transient errors,
/// it opens and writes are refused without touching the database until the break has passed;
/// the caller holds its batch until then. Only transient errors count: a row the database rejects
/// is dealt with by isolation and dead-lettering and must never stop the pipeline.</item>
/// </list>
/// </summary>
internal sealed class CasinoWagerBatchProcessor
{
    private readonly ICasinoWagerWriter _writer;
    private readonly ILogger<CasinoWagerBatchProcessor> _logger;
    private readonly ResiliencePipeline _pipeline;

    public CasinoWagerBatchProcessor(
        ICasinoWagerWriter writer,
        IOptions<ConsumerOptions> options,
        ILogger<CasinoWagerBatchProcessor> logger,
        TimeProvider timeProvider = null)
    {
        _writer = writer;
        _logger = logger;

        var settings = options.Value;
        var time = timeProvider ?? TimeProvider.System;
        Circuit = new CircuitBreakerTracker("SQL Server write", settings.CircuitBreaker, logger, time);

        // The breaker is added first, so it is outermost: one batch that fails after all its retries
        // counts as one failure, and an open circuit refuses the write before any retry starts.
        var builder = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddCircuitBreaker(Circuit.CreateOptions(SqlTransientErrors.IsTransient));

        if (settings.MaxWriteRetries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(SqlTransientErrors.IsTransient),
                    MaxRetryAttempts = settings.MaxWriteRetries,
                    Delay = TimeSpan.FromMilliseconds(settings.RetryBaseDelayMilliseconds),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    OnRetry = args =>
                    {
                        _logger.LogWarning(args.Outcome.Exception, "Transient error writing wagers; retry {Attempt} in {Delay}",
                            args.AttemptNumber + 1, args.RetryDelay);
                        return default;
                    }
                });
        }

        _pipeline = builder.Build();
    }

    /// <summary>State of the SQL Server write circuit breaker.</summary>
    public CircuitBreakerTracker Circuit { get; }

    /// <summary>
    /// True when the batch could not be stored for a reason that will pass (a transient database error
    /// that outlasted the retries, or an open circuit), so the batch must be kept and tried again,
    /// never dead-lettered.
    /// </summary>
    public static bool IsRetryLater(Exception exception) =>
        SqlTransientErrors.IsTransient(exception) || exception is BrokenCircuitException;

    public async Task<BatchResult> ProcessAsync(IReadOnlyList<InboundMessage> batch, CancellationToken cancellationToken)
    {
        var result = new BatchResult();
        var valid = new List<(ulong Tag, CasinoWagerMessage Wager)>(batch.Count);

        foreach (var delivery in batch)
        {
            if (TryParse(delivery, out var wager, out var reason))
            {
                valid.Add((delivery.DeliveryTag, wager));
            }
            else
            {
                result.DeadLetter(delivery.DeliveryTag, reason);
            }
        }

        if (valid.Count == 0)
        {
            return result;
        }

        try
        {
            result.Inserted = await WriteWithRetryAsync(valid.Select(v => v.Wager).ToList(), cancellationToken);
            result.Acknowledged.AddRange(valid.Select(v => v.Tag));
        }
        catch (Exception ex) when (!IsRetryLater(ex) && ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Batch of {Count} wagers was rejected; writing individually to isolate the failure", valid.Count);
            await WriteIndividuallyAsync(valid, result, cancellationToken);
        }

        return result;
    }

    private async Task WriteIndividuallyAsync(List<(ulong Tag, CasinoWagerMessage Wager)> valid, BatchResult result, CancellationToken cancellationToken)
    {
        foreach (var (tag, wager) in valid)
        {
            try
            {
                result.Inserted += await WriteWithRetryAsync([wager], cancellationToken);
                result.Acknowledged.Add(tag);
            }
            catch (Exception ex) when (!IsRetryLater(ex) && ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Wager {WagerId} was rejected by the database and has been dead-lettered", wager.WagerId);
                result.DeadLetter(tag, "rejected by the database");
            }
        }
    }

    private Task<int> WriteWithRetryAsync(IReadOnlyCollection<CasinoWagerMessage> wagers, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(
            static async (state, ct) => await state.Writer.WriteBatchAsync(state.Wagers, ct),
            (Writer: _writer, Wagers: wagers),
            cancellationToken).AsTask();

    private bool TryParse(InboundMessage delivery, out CasinoWagerMessage wager, out string reason)
    {
        try
        {
            wager = MessageSerializer.Deserialize(delivery.Body.Span);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Delivery {DeliveryTag} is not a valid wager message and has been dead-lettered", delivery.DeliveryTag);
            wager = null;
            reason = "malformed message";
            return false;
        }

        if (CasinoWagerMessageRules.IsValid(wager, out reason))
        {
            return true;
        }

        _logger.LogError("Wager {WagerId} failed validation ({Reason}) and has been dead-lettered", wager.WagerId, reason);
        return false;
    }
}
