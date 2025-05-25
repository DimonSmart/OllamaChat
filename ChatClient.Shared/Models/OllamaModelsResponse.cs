using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class OllamaModelsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaModel> Models { get; set; } = [];
}
