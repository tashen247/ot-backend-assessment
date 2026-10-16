using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace OT.Assessment.Infrastructure.Resilience;

/// <summary>
/// Builds the options for one named circuit breaker and keeps what callers need to know about it:
/// its current state, a handle to open or close it by hand, and how long until it lets a trial
/// call through. One instance per breaker, shared for the life of the process, so that every caller
/// sees the same state.
/// </summary>
public sealed class CircuitBreakerTracker
{
    private readonly string _name;
    private readonly CircuitBreakerSettings _settings;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private long _openUntilUtcTicks;

    public CircuitBreakerTracker(string name, CircuitBreakerSettings settings, ILogger logger, TimeProvider time)
    {
        _name = name;
        _settings = settings;
        _logger = logger;
        _time = time;
    }

    public CircuitBreakerStateProvider StateProvider { get; } = new();

    public CircuitBreakerManualControl ManualControl { get; } = new();

    public CircuitState State => StateProvider.CircuitState;

    /// <summary>
    /// True while calls are being refused without being attempted. Polly keeps reporting
    /// <see cref="CircuitState.Open"/> after the break has passed, until the next call moves it to
    /// half-open, so an open circuit whose break is over is not counted as open here; otherwise a
    /// caller that checks this first would never make the trial call and the circuit would never close.
    /// </summary>
    public bool IsOpen => State switch
    {
        CircuitState.Isolated => true,
        CircuitState.Open => RemainingBreak > TimeSpan.Zero,
        _ => false
    };

    /// <summary>
    /// Time until the circuit lets a trial call through; zero when it is not refusing calls. A circuit
    /// opened by hand has no scheduled end, so the configured break duration is given as a hint.
    /// </summary>
    public TimeSpan RetryAfter => State switch
    {
        CircuitState.Isolated => RemainingBreak > TimeSpan.Zero ? RemainingBreak : _settings.BreakDuration,
        CircuitState.Open => RemainingBreak,
        _ => TimeSpan.Zero
    };

    private TimeSpan RemainingBreak
    {
        get
        {
            var remaining = new DateTimeOffset(Interlocked.Read(ref _openUntilUtcTicks), TimeSpan.Zero) - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Circuit breaker options for this breaker. Only exceptions matching <paramref name="countsAsFailure"/>
    /// count towards opening it; everything else (bad data, caller cancellation) passes through untouched.
    /// </summary>
    public CircuitBreakerStrategyOptions CreateOptions(Func<Exception, bool> countsAsFailure) => new()
    {
        FailureRatio = _settings.FailureRatio,
        MinimumThroughput = _settings.MinimumThroughput,
        SamplingDuration = _settings.SamplingDuration,
        BreakDuration = _settings.BreakDuration,
        StateProvider = StateProvider,
        ManualControl = ManualControl,
        ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex && countsAsFailure(ex)),
        OnOpened = args =>
        {
            // A circuit isolated by hand reports an unbounded break; use the configured one as the hint.
            var breakDuration = args.IsManual || args.BreakDuration > TimeSpan.FromDays(1) ? _settings.BreakDuration : args.BreakDuration;
            Interlocked.Exchange(ref _openUntilUtcTicks, (_time.GetUtcNow() + breakDuration).UtcTicks);

            if (args.IsManual)
            {
                _logger.LogWarning("{Circuit} circuit opened by hand; calls are rejected until it is closed", _name);
            }
            else
            {
                _logger.LogWarning(args.Outcome.Exception, "{Circuit} circuit OPEN for {BreakDuration}; calls are rejected without being attempted",
                    _name, breakDuration);
            }

            return default;
        },
        OnHalfOpened = _ =>
        {
            _logger.LogInformation("{Circuit} circuit half-open; letting a trial call through", _name);
            return default;
        },
        OnClosed = args =>
        {
            _logger.LogInformation("{Circuit} circuit closed{How}; calls resume", _name, args.IsManual ? " by hand" : string.Empty);
            return default;
        }
    };
}
