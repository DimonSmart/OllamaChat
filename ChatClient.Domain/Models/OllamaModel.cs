using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class OllamaModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("modified_at")]
    public string ModifiedAt { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string Digest { get; set; } = string.Empty;

    [JsonIgnore]
    public bool SupportsImages { get; set; }

    [JsonIgnore]
    public bool SupportsFunctionCalling { get; set; }

    public override string ToString() => Name;
}
