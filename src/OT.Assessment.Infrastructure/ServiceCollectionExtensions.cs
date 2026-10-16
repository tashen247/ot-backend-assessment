using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Infrastructure.HealthChecks;
using OT.Assessment.Infrastructure.Messaging;
using OT.Assessment.Infrastructure.Persistence;

namespace OT.Assessment.Infrastructure;

public static class ServiceCollectionExtensions
{
    public const string ConnectionStringName = "DatabaseConnection";
    public const string ReadyTag = "ready";

    /// <summary>Registers SQL Server access: connection factory, wager writer and player read repository.</summary>
    public static IServiceCollection AddSqlServerPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
        services.AddSingleton<ICasinoWagerWriter, SqlCasinoWagerWriter>();
        services.AddSingleton<IPlayerReadRepository, SqlPlayerReadRepository>();
        return services;
    }

    /// <summary>Registers the shared RabbitMQ connection and its options.</summary>
    public static IServiceCollection AddRabbitMq(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .Validate(o => o.IsValid(),
                "RabbitMq settings are invalid: HostName and Queue are required, PublisherChannelPoolSize and " +
                "PublishTimeoutMilliseconds must be positive, and PublishCircuitBreaker needs a FailureRatio in (0, 1], " +
                "MinimumThroughput of at least 2 and durations of at least 1 second.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RabbitMqConnectionProvider>();
        return services;
    }

    /// <summary>Registers the confirmed, channel-pooled wager publisher and warms it at start-up.</summary>
    public static IServiceCollection AddRabbitMqPublisher(this IServiceCollection services)
    {
        services.AddSingleton<RabbitMqCasinoWagerPublisher>();
        services.AddSingleton<ICasinoWagerPublisher>(sp => sp.GetRequiredService<RabbitMqCasinoWagerPublisher>());
        services.AddHostedService<RabbitMqPublisherWarmupService>();
        return services;
    }

    /// <summary>Adds readiness checks for SQL Server and RabbitMQ.</summary>
    public static IHealthChecksBuilder AddInfrastructureHealthChecks(this IHealthChecksBuilder builder) =>
        builder
            .AddCheck<SqlServerHealthCheck>("sqlserver", tags: [ReadyTag])
            .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [ReadyTag]);

    /// <summary>Adds a readiness check that reports Degraded while the API is refusing wagers because the publish circuit is open.</summary>
    public static IHealthChecksBuilder AddRabbitMqPublisherHealthCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<RabbitMqPublishCircuitHealthCheck>("rabbitmq-publish-circuit", tags: [ReadyTag]);
}
