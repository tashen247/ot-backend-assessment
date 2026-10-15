using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            .Validate(o => !string.IsNullOrWhiteSpace(o.HostName) && !string.IsNullOrWhiteSpace(o.Queue),
                "RabbitMq:HostName and RabbitMq:Queue are required.")
            .ValidateOnStart();

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
}
