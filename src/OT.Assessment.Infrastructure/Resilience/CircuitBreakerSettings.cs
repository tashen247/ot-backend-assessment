namespace OT.Assessment.Infrastructure.Resilience;

/// <summary>
/// When a circuit breaker opens and how long it stays open. The circuit opens when, within
/// <see cref="SamplingDurationSeconds"/>, at least <see cref="MinimumThroughput"/> calls were made
/// and the share that failed reached <see cref="FailureRatio"/>.
/// </summary>
public sealed class CircuitBreakerSettings
{
    /// <summary>Share of calls that must fail (0 to 1) before the circuit opens.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Calls needed in the sampling window before the ratio is considered. Polly requires at least 2.</summary>
    public int MinimumThroughput { get; set; } = 20;

    public int SamplingDurationSeconds { get; set; } = 10;

    /// <summary>How long the circuit stays open before a single trial call is let through.</summary>
    public int BreakDurationSeconds { get; set; } = 15;

    public TimeSpan SamplingDuration => TimeSpan.FromSeconds(SamplingDurationSeconds);

    public TimeSpan BreakDuration => TimeSpan.FromSeconds(BreakDurationSeconds);

    // Polly's own limits: sampling window of at least 500 ms, break of at least 500 ms.
    public bool IsValid() =>
        FailureRatio is > 0 and <= 1 && MinimumThroughput >= 2 && SamplingDurationSeconds >= 1 && BreakDurationSeconds >= 1;
}
