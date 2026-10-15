namespace OT.Assessment.Core.Abstractions;

/// <summary>Raised when a message could not be handed to the message bus.</summary>
public sealed class MessagePublishException : Exception
{
    public MessagePublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
