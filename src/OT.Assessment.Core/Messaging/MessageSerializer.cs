using System.Text.Json;
using System.Text.Json.Serialization;

namespace OT.Assessment.Core.Messaging;

/// <summary>
/// Single place that defines the wire format of bus messages, so the publisher and the
/// consumer can never disagree. Uses System.Text.Json source generation to avoid
/// reflection on the hot path.
/// </summary>
public static class MessageSerializer
{
    public static byte[] Serialize(CasinoWagerMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, MessagingJsonContext.Default.CasinoWagerMessage);

    /// <exception cref="JsonException">The payload is not a valid casino wager message.</exception>
    public static CasinoWagerMessage Deserialize(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize(body, MessagingJsonContext.Default.CasinoWagerMessage)
        ?? throw new JsonException("Message body deserialised to null.");
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CasinoWagerMessage))]
internal partial class MessagingJsonContext : JsonSerializerContext
{
}
