using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OT.Assessment.App.Caching;
using OT.Assessment.Infrastructure.Resilience;
using Polly.CircuitBreaker;
using StackExchange.Redis;

namespace OT.Assessment.UnitTests.Api;

public class FailOpenOutputCacheStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly IOutputCacheStore _redis = Substitute.For<IOutputCacheStore>();
    private readonly FakeTimeProvider _time = new();
    private readonly FailOpenOutputCacheStore _store;

    public FailOpenOutputCacheStoreTests()
    {
        _store = new FailOpenOutputCacheStore(
            _redis,
            new CachingOptions
            {
                RedisTimeoutMilliseconds = (int)Timeout.TotalMilliseconds,
                RedisCircuitBreaker = new CircuitBreakerSettings
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 2,
                    SamplingDurationSeconds = 60,
                    BreakDurationSeconds = (int)BreakDuration.TotalSeconds
                }
            },
            NullLogger<FailOpenOutputCacheStore>.Instance,
            _time);
    }

    private static RedisConnectionException RedisDown() => new(ConnectionFailureType.UnableToConnect, "No connection is available");

    private void RedisIsDown()
    {
        _redis.GetAsync(default, default).ReturnsForAnyArgs(_ => ValueTask.FromException<byte[]>(RedisDown()));
        _redis.SetAsync(default, default, default, default, default).ReturnsForAnyArgs(_ => ValueTask.FromException(RedisDown()));
        _redis.EvictByTagAsync(default, default).ReturnsForAnyArgs(_ => ValueTask.FromException(RedisDown()));
    }

    private async Task OpenTheCircuitAsync()
    {
        RedisIsDown();
        await _store.GetAsync("k", CancellationToken.None);
        await _store.GetAsync("k", CancellationToken.None);
        Assert.True(_store.Circuit.IsOpen);
    }

    [Fact]
    public async Task Passes_reads_through_while_redis_is_healthy()
    {
        _redis.GetAsync("k", Arg.Any<CancellationToken>()).Returns(new ValueTask<byte[]>([1, 2, 3]));

        Assert.Equal([1, 2, 3], await _store.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task A_redis_error_is_a_cache_miss_and_never_an_exception()
    {
        RedisIsDown();

        Assert.Null(await _store.GetAsync("k", CancellationToken.None));
        await _store.SetAsync("k", [1], ["t"], TimeSpan.FromSeconds(5), CancellationToken.None);
        await _store.EvictByTagAsync("t", CancellationToken.None);
    }

    [Fact]
    public async Task Gives_up_on_a_hung_redis_call_after_the_timeout()
    {
        var never = new TaskCompletionSource<byte[]>();
        _redis.GetAsync(default, default).ReturnsForAnyArgs(new ValueTask<byte[]>(never.Task));

        var read = _store.GetAsync("k", CancellationToken.None).AsTask();
        Assert.False(read.IsCompleted);

        _time.Advance(Timeout + TimeSpan.FromMilliseconds(1));

        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Stops_calling_redis_once_the_circuit_opens()
    {
        await OpenTheCircuitAsync();
        _redis.ClearReceivedCalls();

        Assert.Null(await _store.GetAsync("k", CancellationToken.None));
        await _store.SetAsync("k", [1], ["t"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Empty(_redis.ReceivedCalls());
        Assert.Equal(BreakDuration, _store.Circuit.RetryAfter);
    }

    [Fact]
    public async Task Tries_redis_again_after_the_break_and_closes_when_it_answers()
    {
        await OpenTheCircuitAsync();
        _redis.GetAsync(default, default).ReturnsForAnyArgs(new ValueTask<byte[]>([9]));

        _time.Advance(BreakDuration + TimeSpan.FromSeconds(1));

        Assert.Equal([9], await _store.GetAsync("k", CancellationToken.None));
        Assert.Equal(CircuitState.Closed, _store.Circuit.State);
    }

    [Fact]
    public async Task Resumes_at_once_when_redis_reconnects_instead_of_waiting_out_the_break()
    {
        await OpenTheCircuitAsync();
        _redis.GetAsync(default, default).ReturnsForAnyArgs(new ValueTask<byte[]>([5]));

        await _store.ResumeAsync();

        Assert.Equal(CircuitState.Closed, _store.Circuit.State);
        Assert.Equal([5], await _store.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task Logs_at_most_one_warning_per_interval_while_redis_keeps_failing()
    {
        var logger = new FakeLogger<FailOpenOutputCacheStore>();
        var store = new FailOpenOutputCacheStore(
            _redis,
            new CachingOptions
            {
                // A threshold that is never reached, so every failure gets as far as the logging.
                RedisCircuitBreaker = new CircuitBreakerSettings { FailureRatio = 1.0, MinimumThroughput = 100_000, SamplingDurationSeconds = 60, BreakDurationSeconds = 30 }
            },
            logger,
            _time);
        RedisIsDown();

        for (var i = 0; i < 50; i++)
        {
            await store.GetAsync("k", CancellationToken.None);
        }

        Assert.Equal(1, logger.Collector.Count);

        _time.Advance(FailOpenOutputCacheStore.WarningInterval);
        await store.GetAsync("k", CancellationToken.None);

        Assert.Equal(2, logger.Collector.Count);
        Assert.Contains("49 similar failures", logger.LatestRecord.Message);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_count_against_redis()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.GetAsync("k", cancelled.Token).AsTask());
        }

        Assert.Equal(CircuitState.Closed, _store.Circuit.State);
    }
}
