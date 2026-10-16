using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OT.Assessment.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();

builder.Services
    .AddSqlServerPersistence(builder.Configuration)
    .AddRabbitMq(builder.Configuration)
    .AddRabbitMqPublisher();

builder.Services.AddHealthChecks()
    .AddInfrastructureHealthChecks()
    .AddRabbitMqPublisherHealthCheck();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opts =>
    {
        opts.EnableTryItOutByDefault();
        opts.DocumentTitle = "OT Assessment App";
        opts.DisplayRequestDuration();
    });
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

// Liveness: the process is up. Readiness: SQL Server and RabbitMQ are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(ServiceCollectionExtensions.ReadyTag) });

app.Run();
