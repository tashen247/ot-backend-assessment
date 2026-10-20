using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OT.Assessment.App.Caching;

namespace OT.Assessment.UnitTests.Integration;

/// <summary>Runs only when OT_TEST_REDIS_CONNECTION points at a Redis server, for example <c>localhost:6379</c>.</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "OT_TEST_REDIS_CONNECTION";

    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to run Redis integration tests.";
        }
    }
}

public sealed class RedisCacheIntegrationTests : IAsyncLifetime
{
    private ServiceProvider _provider;
    private IOutputCacheStore _store;

    public Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable(RedisFactAttribute.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return Task.CompletedTask;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Caching:RedisConnectionString"] = connection,
                ["Caching:RedisInstanceName"] = $"ot-test-{Guid.NewGuid():N}:"
            })
            .Build();

        var services = new ServiceCollection().AddLogging();
        services.AddTopSpendersCache(configuration);
        _provider = services.BuildServiceProvider();
        _store = _provider.GetRequiredService<IOutputCacheStore>();
        return Task.CompletedTask;
    }

    [RedisFact]
    public async Task Stores_reads_and_evicts_by_tag_through_the_fail_open_wrapper()
    {
        byte[] value = [1, 2, 3, 4];

        await _store.SetAsync("leaderboard", value, [CachingServiceCollectionExtensions.TopSpendersTag], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(value, await ReadOnceWrittenAsync("leaderboard"));

        await _store.EvictByTagAsync(CachingServiceCollectionExtensions.TopSpendersTag, CancellationToken.None);
        Assert.Null(await _store.GetAsync("leaderboard", CancellationToken.None));
    }

    [RedisFact]
    public async Task Entries_expire_after_their_duration()
    {
        await _store.SetAsync("short", [7], [], TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(await ReadOnceWrittenAsync("short"));

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Null(await _store.GetAsync("short", CancellationToken.None));
    }

    /// <summary>Writes go to Redis in the background, so a read straight after may briefly miss.</summary>
    private async Task<byte[]> ReadOnceWrittenAsync(string key)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await _store.GetAsync(key, CancellationToken.None) is { } value)
            {
                return value;
            }

            await Task.Delay(10);
        }

        return null;
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }
}
