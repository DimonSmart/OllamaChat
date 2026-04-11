using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class VoiceInputSettings
{
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("status")]
    public VoiceInputInitializationStatus Status { get; set; } = VoiceInputInitializationStatus.NotInitialized;

    [JsonPropertyName("recognitionLanguage")]
    public string RecognitionLanguage { get; set; } = "auto";

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = string.Empty;
}
