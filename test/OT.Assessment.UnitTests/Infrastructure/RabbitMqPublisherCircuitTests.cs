using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Infrastructure.Messaging;
using OT.Assessment.Infrastructure.Resilience;
using Polly.CircuitBreaker;

namespace OT.Assessment.UnitTests.Infrastructure;

/// <summary>
/// Runs the real publisher against a port where nothing is listening, so every publish fails the way
/// it does when RabbitMQ is down, without needing a broker.
/// </summary>
public sealed class RabbitMqPublisherCircuitTests : IAsyncLifetime
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(15);

    private readonly FakeTimeProvider _time = new();
    private readonly RabbitMqConnectionProvider _connections;
    private readonly RabbitMqCasinoWagerPublisher _publisher;

    public RabbitMqPublisherCircuitTests()
    {
        var options = Options.Create(new RabbitMqOptions
        {
            HostName = "127.0.0.1",
            Port = 1,
            PublisherChannelPoolSize = 2,
            PublishCircuitBreaker = new CircuitBreakerSettings
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDurationSeconds = 60,
                BreakDurationSeconds = (int)BreakDuration.TotalSeconds
            }
        });

        _connections = new RabbitMqConnectionProvider(options, NullLogger<RabbitMqConnectionProvider>.Instance);
        _publisher = new RabbitMqCasinoWagerPublisher(_connections, options, NullLogger<RabbitMqCasinoWagerPublisher>.Instance, _time);
    }

    private async Task<MessagePublishException> PublishFailsAsync() =>
        await Assert.ThrowsAsync<MessagePublishException>(() => _publisher.PublishAsync(TestData.Wager(), CancellationToken.None));

    [Fact]
    public async Task Failures_below_the_threshold_are_reported_without_a_retry_hint()
    {
        var failure = await PublishFailsAsync();

        Assert.Null(failure.RetryAfter);
        Assert.IsNotType<BrokenCircuitException>(failure.InnerException);
        Assert.Equal(CircuitState.Closed, _publisher.Circuit.State);
    }

    [Fact]
    public async Task Opens_after_repeated_failures_and_then_refuses_immediately_with_a_retry_hint()
    {
        await PublishFailsAsync();
        await PublishFailsAsync();

        var stopwatch = Stopwatch.StartNew();
        var refused = await PublishFailsAsync();
        stopwatch.Stop();

        Assert.Equal(CircuitState.Open, _publisher.Circuit.State);
        Assert.IsType<BrokenCircuitException>(refused.InnerException);
        Assert.Equal(BreakDuration, refused.RetryAfter);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"an open circuit should refuse at once, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Tries_the_broker_again_once_the_break_has_passed()
    {
        await PublishFailsAsync();
        await PublishFailsAsync();

        _time.Advance(BreakDuration + TimeSpan.FromSeconds(1));
        var trial = await PublishFailsAsync();

        // The trial reached the broker (and failed), so the circuit opened again for a fresh break.
        Assert.IsNotType<BrokenCircuitException>(trial.InnerException);
        Assert.Equal(CircuitState.Open, _publisher.Circuit.State);
        Assert.Equal(BreakDuration, _publisher.Circuit.RetryAfter);
    }

    [Fact]
    public async Task Can_be_opened_and_closed_by_hand_as_the_broker_blocks_and_unblocks()
    {
        await _publisher.Circuit.ManualControl.IsolateAsync();
        var refused = await PublishFailsAsync();

        Assert.Equal(CircuitState.Isolated, _publisher.Circuit.State);
        Assert.IsAssignableFrom<BrokenCircuitException>(refused.InnerException);
        Assert.Equal(BreakDuration, refused.RetryAfter);

        await _publisher.Circuit.ManualControl.CloseAsync();
        Assert.Equal(CircuitState.Closed, _publisher.Circuit.State);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _publisher.DisposeAsync();
        await _connections.DisposeAsync();
    }
}
