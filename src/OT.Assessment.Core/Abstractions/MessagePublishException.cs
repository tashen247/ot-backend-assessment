namespace OT.Assessment.Core.Abstractions;

/// <summary>Raised when a message could not be handed to the message bus.</summary>
public sealed class MessagePublishException : Exception
{
    public MessagePublishException(string message, Exception innerException, TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// When the bus is known to be unavailable (an open circuit), how long until it is worth trying
    /// again. Null when the failure gives no such hint.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
