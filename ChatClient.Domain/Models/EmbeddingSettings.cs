using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class EmbeddingSettings
{
    [JsonPropertyName("model")]
    public ServerModelSelection Model { get; set; } = new(null, null);

    [JsonPropertyName("ragMaxTokensPerChunk")]
    public int RagMaxTokensPerChunk { get; set; } = 512;

    [JsonPropertyName("ragOverlapTokens")]
    public int RagOverlapTokens { get; set; } = 64;

    [JsonPropertyName("ragMinRelevanceScore")]
    public double RagMinRelevanceScore { get; set; } = 0.7;
}
