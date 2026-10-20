namespace OT.Assessment.App.Caching;

/// <summary>
/// Starts connecting to Redis as the API starts, without waiting for it. Otherwise the connection is
/// made by the first cache call, and the first requests each wait out the cache timeout while the
/// handshake runs, or, with Redis down, until the connection attempt gives up.
/// </summary>
internal sealed class RedisConnectionWarmup : IHostedService
{
    private readonly RedisConnection _connection;

    public RedisConnectionWarmup(RedisConnection connection) => _connection = connection;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _connection.GetAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
