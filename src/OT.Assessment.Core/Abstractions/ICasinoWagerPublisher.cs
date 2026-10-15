using OT.Assessment.Core.Messaging;

namespace OT.Assessment.Core.Abstractions;

/// <summary>Publishes wager events to the message bus.</summary>
public interface ICasinoWagerPublisher
{
    /// <summary>
    /// Publishes the message and completes only once the broker has confirmed it is stored.
    /// </summary>
    /// <exception cref="MessagePublishException">The broker did not accept the message.</exception>
    Task PublishAsync(CasinoWagerMessage message, CancellationToken cancellationToken);
}
