using Microsoft.AspNetCore.OutputCaching;
using OT.Assessment.Infrastructure.Resilience;
using Polly;
using Polly.CircuitBreaker;

namespace OT.Assessment.App.Caching;

/// <summary>
/// Wraps the Redis output cache so that Redis being slow or down adds at most one short timeout to a
/// request, and nothing at all once it is known to be down.
/// <para>
/// ASP.NET Core's output cache middleware already treats an exception from the store as a miss, so
/// a dead Redis never fails a request on its own. What it does not do is bound how long a call may
/// take, stop calling a Redis that keeps failing, or keep an outage out of the request path, and
/// that is what this adds:
/// </para>
/// <list type="bullet">
/// <item>Every call has a short timeout; a read that fails or times out is a miss.</item>
/// <item>Writes happen in the background. The middleware waits for the store before finishing the
/// request, so an awaited write would add a second timeout to every miss.</item>
/// <item>A circuit breaker stops calling Redis once it keeps failing, and the circuit closes as soon
/// as the Redis client reconnects.</item>
/// <item>Failures are logged at most once per interval.</item>
/// </list>
/// </summary>
internal sealed class FailOpenOutputCacheStore : IOutputCacheStore, IDisposable
{
    /// <summary>
    /// At most one warning per interval. With Redis down every request fails a read and a write, so
    /// hundreds of failures a second can occur before the breaker opens; logging each would flood the log.
    /// </summary>
    internal static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(10);

    private readonly IOutputCacheStore _inner;
    private readonly ILogger<FailOpenOutputCacheStore> _logger;
    private readonly TimeProvider _time;
    private readonly ResiliencePipeline _pipeline;
    private long _nextWarningTicks;
    private int _suppressedWarnings;
    private int _resuming;

    public FailOpenOutputCacheStore(
        IOutputCacheStore inner,
        CachingOptions options,
        ILogger<FailOpenOutputCacheStore> logger,
        TimeProvider timeProvider = null)
    {
        _inner = inner;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;

        Circuit = new CircuitBreakerTracker("Redis cache", options.RedisCircuitBreaker, logger, _time);

        // Breaker outermost, so a call that times out counts as one failure. The pipeline never sees
        // the caller's token (see RunAsync), so every outcome it records is Redis's own.
        _pipeline = new ResiliencePipelineBuilder { TimeProvider = _time }
            .AddCircuitBreaker(Circuit.CreateOptions(_ => true))
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
        // The client reports each of its connections separately, so this can be called twice at once.
        if (Circuit.State == CircuitState.Closed || Interlocked.Exchange(ref _resuming, 1) == 1)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Redis is reachable again; resuming the cache");
            await Circuit.ManualControl.CloseAsync();
        }
        finally
        {
            Volatile.Write(ref _resuming, 0);
        }
    }

    public async ValueTask<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (Circuit.IsOpen)
        {
            return null;
        }

        var read = RunAsync(static (inner, state, token) => inner.GetAsync(state, token), key);

        try
        {
            // The caller can stop waiting at any time; the read itself carries on until Redis answers
            // or the timeout fires, so the breaker records what Redis did, not what the caller did.
            // Otherwise a request that went away during the half-open trial would count as a success
            // and close the circuit on a Redis that is still broken.
            return await read.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveInBackground(read);
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            return null;
        }
    }

    public ValueTask SetAsync(string key, byte[] value, string[] tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        if (!Circuit.IsOpen)
        {
            // Not awaited: the response has been written by now, and the middleware would otherwise hold
            // the request open until Redis answered or the timeout fired.
            _ = WriteInBackgroundAsync(RunAsync(
                static async (inner, state, token) =>
                {
                    await inner.SetAsync(state.Key, state.Value, state.Tags, state.ValidFor, token);
                    return true;
                },
                (Key: key, Value: value, Tags: tags, ValidFor: validFor)));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (Circuit.IsOpen)
        {
            return;
        }

        try
        {
            await RunAsync(
                static async (inner, state, token) =>
                {
                    await inner.EvictByTagAsync(state, token);
                    return true;
                },
                tag).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Entries expire on their own within the configured duration.
            LogFailure(ex);
        }
    }

    /// <summary>
    /// One Redis call through the breaker and the timeout. WaitAsync gives up when the timeout fires
    /// even if the Redis client does not, as with the publisher.
    /// </summary>
    private Task<TResult> RunAsync<TState, TResult>(
        Func<IOutputCacheStore, TState, CancellationToken, ValueTask<TResult>> call, TState state) =>
        _pipeline.ExecuteAsync(
            static async (context, token) => await context.Call(context.Inner, context.State, token).AsTask().WaitAsync(token),
            (Inner: _inner, Call: call, State: state),
            CancellationToken.None).AsTask();

    private async Task WriteInBackgroundAsync(Task<bool> write)
    {
        try
        {
            await write;
        }
        catch (Exception ex)
        {
            // The response has already been served; it simply is not cached this time.
            LogFailure(ex);
        }
    }

    /// <summary>Keeps an abandoned read's eventual failure from going unobserved.</summary>
    private static void ObserveInBackground(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void LogFailure(Exception exception)
    {
        // An open circuit was logged once when it opened; only individual failures before that are logged.
        if (exception is BrokenCircuitException)
        {
            return;
        }

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
