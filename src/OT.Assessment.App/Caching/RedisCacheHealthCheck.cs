using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OT.Assessment.App.Caching;

/// <summary>
/// Reports Degraded, never Unhealthy, when Redis is unreachable: the API still answers everything,
/// it just serves the leaderboard from SQL Server, so it should stay in rotation.
/// </summary>
internal sealed class RedisCacheHealthCheck : IHealthCheck
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);

    private readonly RedisConnection _connection;
    private readonly IOutputCacheStore _store;

    public RedisCacheHealthCheck(RedisConnection connection, IOutputCacheStore store)
    {
        _connection = connection;
        _store = store;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_store is FailOpenOutputCacheStore { Circuit.IsOpen: true } store)
        {
            return HealthCheckResult.Degraded(
                $"Redis cache circuit is open: top spenders served from SQL Server for another {Math.Ceiling(store.Circuit.RetryAfter.TotalSeconds)} s.");
        }

        try
        {
            var multiplexer = await _connection.GetAsync().WaitAsync(PingTimeout, cancellationToken);
            var latency = await multiplexer.GetDatabase().PingAsync().WaitAsync(PingTimeout, cancellationToken);
            return HealthCheckResult.Healthy($"Redis ping {latency.TotalMilliseconds:0.#} ms.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("Redis is unreachable: top spenders served from SQL Server.", ex);
        }
    }
}
