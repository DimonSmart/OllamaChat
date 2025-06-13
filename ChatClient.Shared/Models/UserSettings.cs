using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class UserSettings
{
    /// <summary>
    /// The name of the model to use by default
    /// </summary>
    [JsonPropertyName("defaultModelName")]
    public string DefaultModelName { get; set; } = string.Empty;

    /// <summary>
    /// The default message to prepopulate in the chat input
    /// </summary>
    [JsonPropertyName("defaultChatMessage")]
    public string DefaultChatMessage { get; set; } = string.Empty;

    /// <summary>
    /// Whether to show tokens per second in statistics
    /// </summary>
    [JsonPropertyName("showTokensPerSecond")]
    public bool ShowTokensPerSecond { get; set; } = true;

    /// <summary>
    /// Ollama server URL (including protocol and port)
    /// </summary>
    [JsonPropertyName("ollamaServerUrl")]
    public string OllamaServerUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Basic authentication password for Ollama server
    /// </summary>
    [JsonPropertyName("ollamaBasicAuthPassword")]
    public string OllamaBasicAuthPassword { get; set; } = string.Empty;

    /// <summary>
    /// Whether to ignore SSL certificate errors (for self-signed certificates)
    /// </summary>
    [JsonPropertyName("ignoreSslErrors")]
    public bool IgnoreSslErrors { get; set; } = false;
}
