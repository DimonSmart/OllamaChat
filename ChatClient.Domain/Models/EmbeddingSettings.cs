using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class EmbeddingSettings
{
    [JsonPropertyName("model")]
    public ServerModelSelection Model { get; set; } = new(null, null);

    [JsonPropertyName("ragMinRelevanceScore")]
    public double RagMinRelevanceScore { get; set; } = 0.7;
}
