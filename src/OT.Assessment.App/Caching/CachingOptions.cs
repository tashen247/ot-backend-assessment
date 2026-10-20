using OT.Assessment.Infrastructure.Resilience;
using StackExchange.Redis;

namespace OT.Assessment.App.Caching;

/// <summary>
/// Response caching for the top spenders leaderboard. With a Redis connection string the cache is
/// shared by every API instance; without one each instance keeps its own in memory.
/// </summary>
public sealed class CachingOptions
{
    public const string SectionName = "Caching";

    /// <summary>
    /// How long a top spenders response is served from the cache, so how stale the leaderboard may be.
    /// 0 turns caching off.
    /// </summary>
    public int TopSpendersSeconds { get; set; } = 5;

    /// <summary>StackExchange.Redis configuration, for example <c>localhost:6379</c>. Empty means an in-memory cache per instance.</summary>
    public string RedisConnectionString { get; set; } = string.Empty;

    /// <summary>Prefix for every key, so several applications can share one Redis.</summary>
    public string RedisInstanceName { get; set; } = "ot-assessment:";

    /// <summary>
    /// Longest the API waits for Redis on one cache read or write before treating it as a miss and
    /// carrying on without it. A cache that is slower than the database it protects is no use.
    /// </summary>
    public int RedisTimeoutMilliseconds { get; set; } = 250;

    /// <summary>
    /// When to stop calling Redis altogether and serve every request from SQL Server. The window is
    /// short because the breaker trips on the share of calls failing: on a busy endpoint a long window
    /// full of successful hits from before an outage would hold it closed for longer.
    /// </summary>
    public CircuitBreakerSettings RedisCircuitBreaker { get; set; } = new()
    {
        FailureRatio = 0.5,
        MinimumThroughput = 10,
        SamplingDurationSeconds = 5,
        BreakDurationSeconds = 30
    };

    public bool UseRedis => !string.IsNullOrWhiteSpace(RedisConnectionString);

    public TimeSpan TopSpendersDuration => TimeSpan.FromSeconds(TopSpendersSeconds);

    public TimeSpan RedisTimeout => TimeSpan.FromMilliseconds(RedisTimeoutMilliseconds);

    public bool IsValid() =>
        TopSpendersSeconds >= 0 && RedisTimeoutMilliseconds > 0
        && RedisCircuitBreaker is not null && RedisCircuitBreaker.IsValid()
        && (!UseRedis || IsValidRedisConfiguration(RedisConnectionString));

    /// <summary>
    /// A malformed connection string is a configuration error, not Redis being unavailable, so it is
    /// reported at start-up with the other settings instead of surfacing later from inside the client.
    /// </summary>
    private static bool IsValidRedisConfiguration(string connectionString)
    {
        try
        {
            ConfigurationOptions.Parse(connectionString);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }
}
