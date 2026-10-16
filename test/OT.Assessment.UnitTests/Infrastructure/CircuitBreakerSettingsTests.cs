using OT.Assessment.App.ErrorHandling;
using OT.Assessment.Consumer;
using OT.Assessment.Infrastructure.Messaging;
using OT.Assessment.Infrastructure.Resilience;

namespace OT.Assessment.UnitTests.Infrastructure;

public class CircuitBreakerSettingsTests
{
    [Fact]
    public void Defaults_are_valid_for_both_the_api_and_the_consumer()
    {
        Assert.True(new RabbitMqOptions().IsValid());
        Assert.True(new ConsumerOptions().IsValid());
    }

    [Theory]
    [InlineData(0.0, 2, 10, 15)]   // a ratio of zero would open on the first call
    [InlineData(1.5, 2, 10, 15)]   // a ratio above one can never be reached
    [InlineData(0.5, 1, 10, 15)]   // Polly needs at least two calls in the window
    [InlineData(0.5, 2, 0, 15)]
    [InlineData(0.5, 2, 10, 0)]
    public void Rejects_settings_polly_would_refuse_or_that_make_no_sense(double ratio, int throughput, int sampling, int breakSeconds)
    {
        var settings = new CircuitBreakerSettings
        {
            FailureRatio = ratio,
            MinimumThroughput = throughput,
            SamplingDurationSeconds = sampling,
            BreakDurationSeconds = breakSeconds
        };

        Assert.False(settings.IsValid());
        Assert.False(new RabbitMqOptions { PublishCircuitBreaker = settings }.IsValid());
        Assert.False(new ConsumerOptions { CircuitBreaker = settings }.IsValid());
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0.0, 1)]
    [InlineData(0.2, 1)]
    [InlineData(14.01, 15)]
    public void Retry_after_is_whole_seconds_rounded_up_and_at_least_one(double? seconds, int expected) =>
        Assert.Equal(expected, MessagePublishExceptionHandler.RetryAfterSeconds(seconds is { } s ? TimeSpan.FromSeconds(s) : null));
}
