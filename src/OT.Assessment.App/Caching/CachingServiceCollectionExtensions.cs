using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.OutputCaching.StackExchangeRedis;
using OT.Assessment.Infrastructure;

namespace OT.Assessment.App.Caching;

internal static class CachingServiceCollectionExtensions
{
    /// <summary>Output cache policy applied to <c>GET api/player/topSpenders</c>.</summary>
    public const string TopSpendersPolicy = "TopSpenders";

    public const string TopSpendersTag = "top-spenders";

    /// <summary>
    /// Registers the output cache and the top spenders policy. With a Redis connection string the
    /// store is Redis, wrapped so that a Redis failure is a cache miss rather than an error; without
    /// one it is the in-memory store.
    /// </summary>
    public static IServiceCollection AddTopSpendersCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CachingOptions>()
            .Bind(configuration.GetSection(CachingOptions.SectionName))
            .Validate(o => o.IsValid(),
                "Caching settings are invalid: TopSpendersSeconds must be 0 or more, RedisTimeoutMilliseconds positive, " +
                "and RedisCircuitBreaker needs a FailureRatio in (0, 1], MinimumThroughput of at least 2 and durations of at least 1 second.")
            .ValidateOnStart();

        // The policy and store are chosen at registration, so the settings are read here as well.
        var settings = configuration.GetSection(CachingOptions.SectionName).Get<CachingOptions>() ?? new CachingOptions();

        if (settings.UseRedis)
        {
            services.AddSingleton(sp => new RedisConnection(settings, sp.GetRequiredService<ILogger<RedisConnection>>()));

            services.AddStackExchangeRedisOutputCache(o => o.InstanceName = settings.RedisInstanceName);
            services.AddOptions<RedisOutputCacheOptions>()
                .Configure<RedisConnection>((o, connection) => o.ConnectionMultiplexerFactory = connection.GetAsync);

            // Microsoft's Redis store is internal, so it is wrapped by replacing its registration.
            var redisStore = services.Last(d => d.ServiceType == typeof(IOutputCacheStore));
            services.Remove(redisStore);
            services.AddSingleton<IOutputCacheStore>(sp =>
            {
                var store = new FailOpenOutputCacheStore(
                    (IOutputCacheStore)ActivatorUtilities.CreateInstance(sp, redisStore.ImplementationType),
                    settings,
                    sp.GetRequiredService<ILogger<FailOpenOutputCacheStore>>(),
                    sp.GetService<TimeProvider>());

                sp.GetRequiredService<RedisConnection>().Restored += () => _ = store.ResumeAsync();
                return store;
            });

            services.AddHealthChecks()
                .AddCheck<RedisCacheHealthCheck>("redis-cache", tags: [ServiceCollectionExtensions.ReadyTag]);
        }

        // Adds the in-memory store only if no store is registered yet.
        services.AddOutputCache(options => options.AddPolicy(TopSpendersPolicy, policy =>
        {
            if (settings.TopSpendersSeconds == 0)
            {
                policy.NoCache();
                return;
            }

            // Only 200 responses to GET are cached (the default policy), so a 400 for a bad count never is.
            // The key ignores the host name (included by default), so every API instance shares one
            // entry per count however it is addressed: the leaderboard is the same whichever host is asked.
            policy.Expire(settings.TopSpendersDuration)
                .SetVaryByHost(false)
                .SetVaryByQuery("count")
                .Tag(TopSpendersTag);
        }));

        return services;
    }
}
