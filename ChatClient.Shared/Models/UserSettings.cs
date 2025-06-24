using System.Text.Json.Serialization;

using ChatClient.Shared.Constants;

namespace ChatClient.Shared.Models;

public class UserSettings
{
    [JsonPropertyName("defaultModelName")]
    public string DefaultModelName { get; set; } = string.Empty;

    [JsonPropertyName("defaultChatMessage")]
    public string DefaultChatMessage { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("agentName")]
    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    /// Ollama server URL (including protocol and port)
    /// </summary>
    [JsonPropertyName("ollamaServerUrl")]
    public string OllamaServerUrl { get; set; } = OllamaDefaults.ServerUrl;

    [JsonPropertyName("ollamaBasicAuthPassword")]
    public string OllamaBasicAuthPassword { get; set; } = string.Empty;

    /// <summary>
    /// Whether to ignore SSL certificate errors (for self-signed certificates)
    /// </summary>
    [JsonPropertyName("ignoreSslErrors")]
    public bool IgnoreSslErrors { get; set; } = false;

    /// <summary>
    /// HTTP request timeout in seconds for Ollama API calls
    /// </summary>
    [JsonPropertyName("httpTimeoutSeconds")]
    public int HttpTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// HTTP request timeout in seconds for MCP sampling requests (typically longer than regular API calls)
    /// </summary>
    [JsonPropertyName("mcpSamplingTimeoutSeconds")]
    public int McpSamplingTimeoutSeconds { get; set; } = 30 * 60;

    /// <summary>
    /// Default mode for new chats: true for Agent mode, false for Ask mode
    /// </summary>
    [JsonPropertyName("defaultUseAgentMode")]
    public bool DefaultUseAgentMode { get; set; } = false;
}
