using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OT.Assessment.App.Caching;
using OT.Assessment.Core.Models;

namespace OT.Assessment.UnitTests.Api;

public class CachingRegistrationTests
{
    private static ServiceProvider Build(string redisConnectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Caching:RedisConnectionString"] = redisConnectionString })
            .Build();

        var services = new ServiceCollection().AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddTopSpendersCache(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Uses_the_in_memory_store_when_no_redis_is_configured()
    {
        using var provider = Build(string.Empty);

        Assert.IsNotType<FailOpenOutputCacheStore>(provider.GetRequiredService<IOutputCacheStore>());
    }

    [Fact]
    public void Wraps_the_redis_store_so_it_fails_open_when_redis_is_configured()
    {
        using var provider = Build("localhost:6379");

        Assert.IsType<FailOpenOutputCacheStore>(provider.GetRequiredService<IOutputCacheStore>());
    }

    [Fact]
    public async Task With_redis_configured_but_unreachable_a_read_is_a_quick_miss()
    {
        // Nothing listens on port 1: this is the real Redis store, connection and wrapper as the API builds them.
        await using var provider = Build("127.0.0.1:1");
        var store = provider.GetRequiredService<IOutputCacheStore>();

        var stopwatch = Stopwatch.StartNew();
        var value = await store.GetAsync("top-spenders", CancellationToken.None);
        stopwatch.Stop();

        Assert.Null(value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"a cache miss should not wait on Redis, took {stopwatch.Elapsed}");
    }
}

/// <summary>Hosts the API with a Redis cache that fails every call.</summary>
public sealed class RedisDownApiFactory : ApiFactory
{
    protected override void ReplaceServices(IServiceCollection services)
    {
        var redis = Substitute.For<IOutputCacheStore>();
        redis.GetAsync(default, default).ReturnsForAnyArgs(_ => ValueTask.FromException<byte[]>(new InvalidOperationException("Redis is down")));
        redis.SetAsync(default, default, default, default, default).ReturnsForAnyArgs(_ => ValueTask.FromException(new InvalidOperationException("Redis is down")));

        services.AddSingleton<IOutputCacheStore>(sp => new FailOpenOutputCacheStore(
            redis, new CachingOptions(), sp.GetRequiredService<ILogger<FailOpenOutputCacheStore>>()));
    }
}

public class RedisDownApiTests : IClassFixture<RedisDownApiFactory>
{
    private readonly RedisDownApiFactory _factory;

    public RedisDownApiTests(RedisDownApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Top_spenders_is_still_served_from_the_database_when_redis_fails()
    {
        _factory.Players.GetTopSpendersAsync(5, Arg.Any<CancellationToken>()).Returns(
            [new TopSpender { AccountId = Guid.NewGuid(), Username = "fromSql", TotalAmountSpend = 1m }]);
        using var client = _factory.CreateClient();

        var first = await client.GetAsync("/api/player/topSpenders?count=5");
        var second = await client.GetAsync("/api/player/topSpenders?count=5");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("fromSql", await second.Content.ReadAsStringAsync());
        await _factory.Players.Received(2).GetTopSpendersAsync(5, Arg.Any<CancellationToken>());
    }
}
