using OT.Assessment.Consumer;
using OT.Assessment.Consumer.Processing;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Infrastructure;
using OT.Assessment.Infrastructure.Messaging;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // appsettings.json is copied to the output folder, so resolve it from there regardless of the working directory.
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services
    .AddSqlServerPersistence(builder.Configuration)
    .AddRabbitMq(builder.Configuration);

builder.Services.AddOptions<ConsumerOptions>()
    .Bind(builder.Configuration.GetSection(ConsumerOptions.SectionName))
    .Validate(o => o.IsValid(), "Consumer settings are invalid: PrefetchCount must be at least BatchSize and at most 65535.")
    .ValidateOnStart();

builder.Services.AddSingleton<RabbitMqDeadLetterPublisher>();
builder.Services.AddSingleton<IDeadLetterPublisher>(sp => sp.GetRequiredService<RabbitMqDeadLetterPublisher>());
builder.Services.AddSingleton<CasinoWagerBatchProcessor>();
builder.Services.AddSingleton<DeliverySettler>();
builder.Services.AddHostedService<CasinoWagerConsumerWorker>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Application started {Time:yyyy-MM-dd HH:mm:ss}", DateTime.Now);

await host.RunAsync();

logger.LogInformation("Application ended {Time:yyyy-MM-dd HH:mm:ss}", DateTime.Now);
