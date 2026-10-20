using Microsoft.AspNetCore.OutputCaching;
using OT.Assessment.Infrastructure.Resilience;
using Polly;
using Polly.CircuitBreaker;

namespace OT.Assessment.App.Caching;

/// <summary>
/// Wraps the Redis output cache so that Redis being slow or down never fails or slows a request.
/// <para>
/// Every call is bounded by a short timeout. A failed read is treated as a miss, so the request is
/// served from SQL Server as it would be with no cache, and a failed write or eviction is dropped.
/// A circuit breaker stops calling Redis at all once it keeps failing, so an outage costs nothing
/// per request while it lasts, and lets a trial call through after the break to find out whether
/// it has recovered.
/// </para>
/// </summary>
internal sealed class FailOpenOutputCacheStore : IOutputCacheStore, IDisposable
{
    /// <summary>
    /// At most one warning per interval. With Redis down every request fails a read and a write, so
    /// hundreds of failures a second can occur before the breaker opens; logging each would flood the log.
    /// </summary>
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(10);

    private readonly IOutputCacheStore _inner;
    private readonly TimeProvider _time;
    private long _nextWarningTicks;
    private int _suppressedWarnings;
    private readonly ILogger<FailOpenOutputCacheStore> _logger;
    private readonly ResiliencePipeline _pipeline;

    public FailOpenOutputCacheStore(
        IOutputCacheStore inner,
        CachingOptions options,
        ILogger<FailOpenOutputCacheStore> logger,
        TimeProvider timeProvider = null)
    {
        _inner = inner;
        _logger = logger;
        var time = timeProvider ?? TimeProvider.System;
        _time = time;

        Circuit = new CircuitBreakerTracker("Redis cache", options.RedisCircuitBreaker, logger, time);

        // Breaker outermost, so a call that times out counts as one failure. Caller cancellation
        // (the request went away) says nothing about Redis and is not counted.
        _pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddCircuitBreaker(Circuit.CreateOptions(ex => ex is not OperationCanceledException))
            .AddTimeout(options.RedisTimeout)
            .Build();
    }

    /// <summary>State of the Redis circuit breaker, for health reporting.</summary>
    public CircuitBreakerTracker Circuit { get; }

    /// <summary>
    /// Closes the circuit when the Redis client reports that it has reconnected, so caching resumes
    /// as soon as Redis is back instead of when the break runs out. Without this, SQL Server would
    /// carry the leaderboard for the rest of the break for nothing. The break still applies to a
    /// Redis that stays connected but answers too slowly.
    /// </summary>
    public async Task ResumeAsync()
    {
        if (Circuit.State != CircuitState.Closed)
        {
            _logger.LogInformation("Redis is reachable again; resuming the cache");
            await Circuit.ManualControl.CloseAsync();
        }
    }

    public async ValueTask<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (Circuit.IsOpen)
        {
            return null;
        }

        try
        {
            // WaitAsync gives up when the timeout fires even if the client does not, as with the publisher.
            return await _pipeline.ExecuteAsync(
                static async (state, token) => await state.Inner.GetAsync(state.Key, token).AsTask().WaitAsync(token),
                (Inner: _inner, Key: key),
                cancellationToken);
        }
        catch (Exception ex) when (IsCacheFailure(ex, cancellationToken))
        {
            return null;
        }
    }

    public async ValueTask SetAsync(string key, byte[] value, string[] tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        if (Circuit.IsOpen)
        {
            return;
        }

        try
        {
            await _pipeline.ExecuteAsync(
                static async (state, token) =>
                    await state.Inner.SetAsync(state.Key, state.Value, state.Tags, state.ValidFor, token).AsTask().WaitAsync(token),
                (Inner: _inner, Key: key, Value: value, Tags: tags, ValidFor: validFor),
                cancellationToken);
        }
        catch (Exception ex) when (IsCacheFailure(ex, cancellationToken))
        {
            // The response has already been served; it simply is not cached this time.
        }
    }

    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (Circuit.IsOpen)
        {
            return;
        }

        try
        {
            await _pipeline.ExecuteAsync(
                static async (state, token) => await state.Inner.EvictByTagAsync(state.Tag, token).AsTask().WaitAsync(token),
                (Inner: _inner, Tag: tag),
                cancellationToken);
        }
        catch (Exception ex) when (IsCacheFailure(ex, cancellationToken))
        {
            // Entries expire on their own within the configured duration.
        }
    }

    /// <summary>
    /// True for anything that should be absorbed: Redis errors, timeouts and an open circuit. False
    /// when the caller cancelled, so that cancellation propagates as it would without a cache.
    /// </summary>
    private bool IsCacheFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // An open circuit was logged once when it opened; only individual failures before that are logged.
        if (exception is not BrokenCircuitException)
        {
            LogFailure(exception);
        }

        return true;
    }

    private void LogFailure(Exception exception)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var next = Interlocked.Read(ref _nextWarningTicks);

        if (now < next || Interlocked.CompareExchange(ref _nextWarningTicks, now + WarningInterval.Ticks, next) != next)
        {
            Interlocked.Increment(ref _suppressedWarnings);
            return;
        }

        var suppressed = Interlocked.Exchange(ref _suppressedWarnings, 0);
        _logger.LogWarning(
            "Redis cache call failed ({Error}); continuing without the cache. {Suppressed} similar failures since the previous warning",
            exception.Message, suppressed);
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
