using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OT.Assessment.App.Controllers;
using OT.Assessment.Core.Abstractions;

namespace OT.Assessment.UnitTests.Api;

/// <summary>Hosts the real API pipeline (routing, model binding, validation, error handling) with the bus and database substituted.</summary>
public class ApiFactory : WebApplicationFactory<PlayerController>
{
    public ICasinoWagerPublisher Publisher { get; } = Substitute.For<ICasinoWagerPublisher>();
    public IPlayerReadRepository Players { get; } = Substitute.For<IPlayerReadRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // The in-memory output cache: the suite must not depend on a Redis server.
        builder.UseSetting("Caching:RedisConnectionString", string.Empty);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<ICasinoWagerPublisher>();
            services.RemoveAll<IPlayerReadRepository>();
            services.AddSingleton(Publisher);
            services.AddSingleton(Players);
            ReplaceServices(services);
        });
    }

    /// <summary>Lets a derived factory substitute further services.</summary>
    protected virtual void ReplaceServices(IServiceCollection services)
    {
    }
}
