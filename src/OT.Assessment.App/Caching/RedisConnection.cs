using StackExchange.Redis;

namespace OT.Assessment.App.Caching;

/// <summary>
/// The one Redis connection for the process, shared by the output cache and its health check.
/// Opened on first use and configured so that Redis being down never stops the API starting or
/// holds up a request.
/// </summary>
internal sealed class RedisConnection : IAsyncDisposable, IDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;

    /// <summary>Raised when the client re-establishes a connection to Redis after losing it.</summary>
    public event Action Restored;

    public RedisConnection(CachingOptions options, ILogger<RedisConnection> logger)
    {
        var configuration = ConfigurationOptions.Parse(options.RedisConnectionString);

        // Start even if Redis is down, and keep reconnecting in the background.
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = 2000;

        // While disconnected, fail commands at once instead of queueing them until Redis returns:
        // a cache read that waits is worse than a miss.
        configuration.BacklogPolicy = BacklogPolicy.FailFast;

        _connection = new Lazy<Task<IConnectionMultiplexer>>(async () =>
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
            multiplexer.ConnectionRestored += (_, args) =>
            {
                logger.LogInformation("Redis connection restored ({Endpoint})", args.EndPoint);
                Restored?.Invoke();
            };
            return multiplexer;
        });
    }

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    public void Dispose()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            _connection.Value.Result.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            await _connection.Value.Result.DisposeAsync();
        }
    }
}
