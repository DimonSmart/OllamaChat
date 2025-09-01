using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class EmbeddingSettings
{
    [JsonPropertyName("model")]
    public ServerModel Model { get; set; } = new(Guid.Empty, string.Empty);

    [JsonPropertyName("ragLineChunkSize")]
    public int RagLineChunkSize { get; set; } = 256;

    [JsonPropertyName("ragParagraphChunkSize")]
    public int RagParagraphChunkSize { get; set; } = 512;

    [JsonPropertyName("ragParagraphOverlap")]
    public int RagParagraphOverlap { get; set; } = 64;
}
