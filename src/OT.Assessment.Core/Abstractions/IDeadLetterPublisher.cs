namespace OT.Assessment.Core.Abstractions;

/// <summary>
/// Publishes a message the consumer cannot store. The caller acks the original delivery
/// only after this method returns, which is only after the broker confirms.
/// </summary>
public interface IDeadLetterPublisher
{
    Task PublishConfirmedAsync(ReadOnlyMemory<byte> body, string reason, CancellationToken cancellationToken);
}
