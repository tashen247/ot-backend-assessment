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
    public void Refuses_a_malformed_redis_connection_string_with_a_clear_message()
    {
        var failure = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => Build("localhost:6379,connectTimeout=abc"));

        Assert.Contains("RedisConnectionString", failure.Message);
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

/// <summary>
/// Hosts the API with a Redis cache that never answers. ASP.NET Core's output cache middleware already
/// treats a store that throws as a miss, so a hung store is what shows the wrapper's own value: the
/// timeout, and the breaker that then stops calling Redis.
/// </summary>
public sealed class HungRedisApiFactory : ApiFactory
{
    public IOutputCacheStore Redis { get; } = Substitute.For<IOutputCacheStore>();

    protected override void ReplaceServices(IServiceCollection services)
    {
        Redis.GetAsync(default, default).ReturnsForAnyArgs(new ValueTask<byte[]>(new TaskCompletionSource<byte[]>().Task));
        Redis.SetAsync(default, default, default, default, default).ReturnsForAnyArgs(new ValueTask(new TaskCompletionSource().Task));

        services.AddSingleton<IOutputCacheStore>(sp => new FailOpenOutputCacheStore(
            Redis,
            new CachingOptions
            {
                RedisTimeoutMilliseconds = 300,
                RedisCircuitBreaker = new() { FailureRatio = 0.5, MinimumThroughput = 2, SamplingDurationSeconds = 5, BreakDurationSeconds = 30 }
            },
            sp.GetRequiredService<ILogger<FailOpenOutputCacheStore>>()));
    }
}

public class HungRedisApiTests : IClassFixture<HungRedisApiFactory>
{
    private readonly HungRedisApiFactory _factory;

    public HungRedisApiTests(HungRedisApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_hung_redis_costs_at_most_one_timeout_and_then_nothing()
    {
        _factory.Players.GetTopSpendersAsync(5, Arg.Any<CancellationToken>()).Returns(
            [new TopSpender { AccountId = Guid.NewGuid(), Username = "fromSql", TotalAmountSpend = 1m }]);
        _factory.Players.GetCasinoWagersAsync(default, default, default, default).ReturnsForAnyArgs(
            new PagedResult<PlayerWager>([], page: 1, pageSize: 10, total: 0));
        using var client = _factory.CreateClient();

        // Warm the server up on an uncached endpoint, so start-up cost is not mistaken for Redis.
        await client.GetAsync($"/api/player/{Guid.NewGuid()}/casino");

        var first = Stopwatch.StartNew();
        var response = await client.GetAsync("/api/player/topSpenders?count=5");
        first.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("fromSql", await response.Content.ReadAsStringAsync());
        // One 300 ms read timeout. Had the write been awaited too, it would be at least 600 ms.
        Assert.True(first.Elapsed >= TimeSpan.FromMilliseconds(290) && first.Elapsed < TimeSpan.FromMilliseconds(550),
            $"first request took {first.Elapsed}");

        // The read and the background write both time out, which opens the circuit.
        await Task.Delay(700);
        var callsBefore = _factory.Redis.ReceivedCalls().Count();

        for (var i = 0; i < 3; i++)
        {
            var request = Stopwatch.StartNew();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/player/topSpenders?count=5")).StatusCode);
            Assert.True(request.Elapsed < TimeSpan.FromMilliseconds(250), $"request with the circuit open took {request.Elapsed}");
        }

        Assert.Equal(callsBefore, _factory.Redis.ReceivedCalls().Count());
        await _factory.Players.Received(4).GetTopSpendersAsync(5, Arg.Any<CancellationToken>());
    }
}

public class CachingOptionsTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("localhost:6379", true)]
    [InlineData("redis-1:6379,redis-2:6379,password=secret,ssl=true", true)]
    [InlineData("localhost:6379,connectTimeout=abc", false)]
    public void Validates_the_redis_connection_string(string connectionString, bool valid) =>
        Assert.Equal(valid, new CachingOptions { RedisConnectionString = connectionString }.IsValid());
}
