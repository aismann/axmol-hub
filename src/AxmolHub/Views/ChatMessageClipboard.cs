using System.Text.Json;
using Avalonia.Input;
using AxmolHub.Core;

namespace AxmolHub;

/// <summary>Hub-private clipboard data retains attachment identity inside Hub and carries bytes so another
/// conversation can import the image without depending on the source session's path.</summary>
internal sealed record ChatMessageClipboardPayload(string Text, string ConversationId, IReadOnlyList<ChatMessageClipboardImage> Images);
internal sealed record ChatMessageClipboardImage(ChatImage Attachment, byte[] Bytes);

internal static class ChatMessageClipboard
{
    internal static readonly DataFormat<byte[]> Format = DataFormat.CreateBytesApplicationFormat("AxmolHub.ChatMessage.v1");

    internal static byte[] Serialize(ChatMessageClipboardPayload payload) => JsonSerializer.SerializeToUtf8Bytes(payload);

    internal static ChatMessageClipboardPayload? TryDeserialize(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ChatMessageClipboardPayload>(bytes);
            if (payload is null || string.IsNullOrWhiteSpace(payload.ConversationId) || payload.Images is null
                || payload.Images.Count > ChatImageFormat.MaxImagesPerMessage)
                return null;
            foreach (var image in payload.Images)
            {
                var kind = ChatImageFormat.Identify(image.Bytes);
                if (kind is null || image.Bytes.LongLength > ChatImageFormat.MaxImageBytes
                    || image.Attachment.MediaType != kind.Value.MediaType
                    || image.Attachment.Bytes != image.Bytes.LongLength
                    || Path.GetFileName(image.Attachment.File) != image.Attachment.File)
                    return null;
            }
            return payload;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
