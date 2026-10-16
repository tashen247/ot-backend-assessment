using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OT.Assessment.Consumer;
using OT.Assessment.Consumer.Processing;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;
using OT.Assessment.Infrastructure.Resilience;
using Polly.CircuitBreaker;

namespace OT.Assessment.UnitTests.Consumer;

public class CasinoWagerBatchProcessorCircuitTests
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly ICasinoWagerWriter _writer = Substitute.For<ICasinoWagerWriter>();
    private readonly FakeTimeProvider _time = new();
    private readonly CasinoWagerBatchProcessor _processor;

    public CasinoWagerBatchProcessorCircuitTests()
    {
        // No retries, so each failed batch is one call to the writer and nothing waits on the fake clock.
        _processor = new CasinoWagerBatchProcessor(
            _writer,
            Options.Create(new ConsumerOptions
            {
                MaxWriteRetries = 0,
                CircuitBreaker = new CircuitBreakerSettings
                {
                    FailureRatio = 1.0,
                    MinimumThroughput = 2,
                    SamplingDurationSeconds = 60,
                    BreakDurationSeconds = (int)BreakDuration.TotalSeconds
                }
            }),
            NullLogger<CasinoWagerBatchProcessor>.Instance,
            _time);
    }

    private static InboundMessage[] Batch() => [new(1, MessageSerializer.Serialize(TestData.Wager()))];

    private void DatabaseIsDown() =>
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException());

    private void DatabaseIsUp() =>
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<CasinoWagerMessage>>().Count);

    private async Task OpenTheCircuitAsync()
    {
        DatabaseIsDown();
        await Assert.ThrowsAsync<TimeoutException>(() => _processor.ProcessAsync(Batch(), CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() => _processor.ProcessAsync(Batch(), CancellationToken.None));
    }

    [Fact]
    public async Task Opens_after_batches_keep_failing_and_then_refuses_without_touching_the_database()
    {
        await OpenTheCircuitAsync();
        _writer.ClearReceivedCalls();

        var refused = await Assert.ThrowsAsync<BrokenCircuitException>(() => _processor.ProcessAsync(Batch(), CancellationToken.None));

        await _writer.DidNotReceiveWithAnyArgs().WriteBatchAsync(default, default);
        Assert.True(_processor.Circuit.IsOpen);
        Assert.Equal(BreakDuration, _processor.Circuit.RetryAfter);
        Assert.True(CasinoWagerBatchProcessor.IsRetryLater(refused), "an open circuit must lead to the batch being held, never dead-lettered");
    }

    [Fact]
    public async Task Rows_the_database_rejects_are_dead_lettered_and_never_open_the_circuit()
    {
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("constraint violated"));

        for (var i = 0; i < 5; i++)
        {
            var result = await _processor.ProcessAsync(Batch(), CancellationToken.None);
            Assert.Single(result.DeadLettered);
        }

        Assert.Equal(CircuitState.Closed, _processor.Circuit.State);
    }

    [Fact]
    public async Task Lets_a_trial_batch_through_after_the_break_and_closes_when_it_succeeds()
    {
        await OpenTheCircuitAsync();
        DatabaseIsUp();

        _time.Advance(BreakDuration + TimeSpan.FromSeconds(1));
        var result = await _processor.ProcessAsync(Batch(), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(CircuitState.Closed, _processor.Circuit.State);
        Assert.Equal(TimeSpan.Zero, _processor.Circuit.RetryAfter);
    }

    [Fact]
    public async Task Reopens_when_the_trial_batch_fails()
    {
        await OpenTheCircuitAsync();

        _time.Advance(BreakDuration + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => _processor.ProcessAsync(Batch(), CancellationToken.None));

        Assert.Equal(CircuitState.Open, _processor.Circuit.State);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => _processor.ProcessAsync(Batch(), CancellationToken.None));
    }

    [Fact]
    public async Task One_failure_amongst_successes_does_not_open_the_circuit()
    {
        DatabaseIsUp();
        await _processor.ProcessAsync(Batch(), CancellationToken.None);

        DatabaseIsDown();
        await Assert.ThrowsAsync<TimeoutException>(() => _processor.ProcessAsync(Batch(), CancellationToken.None));

        Assert.Equal(CircuitState.Closed, _processor.Circuit.State);
    }
}
