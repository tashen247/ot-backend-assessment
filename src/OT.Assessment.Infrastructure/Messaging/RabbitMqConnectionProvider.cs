using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace OT.Assessment.Infrastructure.Messaging;

/// <summary>
/// Owns the single long-lived AMQP connection for the process. Connections are expensive and
/// meant to be shared; channels are opened from it as needed. Automatic recovery restores the
/// connection and its channels after a broker restart or network blip.
/// </summary>
public sealed class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection _connection;

    public RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnectionProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null)
            {
                var factory = new ConnectionFactory
                {
                    HostName = _options.HostName,
                    Port = _options.Port,
                    UserName = _options.UserName,
                    Password = _options.Password,
                    VirtualHost = _options.VirtualHost,
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    ClientProvidedName = AppDomain.CurrentDomain.FriendlyName
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", _options.HostName, _options.Port);
            }

            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }

        _lock.Dispose();
    }
}
