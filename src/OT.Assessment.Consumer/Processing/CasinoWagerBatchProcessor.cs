using System.Text.Json;
using Microsoft.Extensions.Options;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;
using Polly;
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
/// <item>If transient errors outlast the retries, the exception propagates and the caller requeues the batch.</item>
/// </list>
/// </summary>
internal sealed class CasinoWagerBatchProcessor
{
    private readonly ICasinoWagerWriter _writer;
    private readonly ILogger<CasinoWagerBatchProcessor> _logger;
    private readonly ResiliencePipeline _retry;

    public CasinoWagerBatchProcessor(ICasinoWagerWriter writer, IOptions<ConsumerOptions> options, ILogger<CasinoWagerBatchProcessor> logger)
    {
        _writer = writer;
        _logger = logger;

        var settings = options.Value;
        _retry = settings.MaxWriteRetries == 0
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
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
                })
                .Build();
    }

    public async Task<BatchResult> ProcessAsync(IReadOnlyList<InboundMessage> batch, CancellationToken cancellationToken)
    {
        var result = new BatchResult();
        var valid = new List<(ulong Tag, CasinoWagerMessage Wager)>(batch.Count);

        foreach (var delivery in batch)
        {
            if (TryParse(delivery, out var wager))
            {
                valid.Add((delivery.DeliveryTag, wager));
            }
            else
            {
                result.DeadLettered.Add(delivery.DeliveryTag);
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
        catch (Exception ex) when (!SqlTransientErrors.IsTransient(ex) && ex is not OperationCanceledException)
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
            catch (Exception ex) when (!SqlTransientErrors.IsTransient(ex) && ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Wager {WagerId} was rejected by the database and has been dead-lettered", wager.WagerId);
                result.DeadLettered.Add(tag);
            }
        }
    }

    private Task<int> WriteWithRetryAsync(IReadOnlyCollection<CasinoWagerMessage> wagers, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(
            static async (state, ct) => await state.Writer.WriteBatchAsync(state.Wagers, ct),
            (Writer: _writer, Wagers: wagers),
            cancellationToken).AsTask();

    private bool TryParse(InboundMessage delivery, out CasinoWagerMessage wager)
    {
        try
        {
            wager = MessageSerializer.Deserialize(delivery.Body.Span);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Delivery {DeliveryTag} is not a valid wager message and has been dead-lettered", delivery.DeliveryTag);
            wager = null;
            return false;
        }

        if (CasinoWagerMessageRules.IsValid(wager, out var reason))
        {
            return true;
        }

        _logger.LogError("Wager {WagerId} failed validation ({Reason}) and has been dead-lettered", wager.WagerId, reason);
        return false;
    }
}
