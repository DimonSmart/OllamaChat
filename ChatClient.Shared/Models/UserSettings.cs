using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class UserSettings
{
    [JsonPropertyName("defaultChatMessage")]
    public string DefaultChatMessage { get; set; } = string.Empty;

    [JsonPropertyName("defaultMultiAgentChatMessage")]
    public string DefaultMultiAgentChatMessage { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("agentName")]
    public string AgentName { get; set; } = string.Empty;

    [JsonPropertyName("defaultModel")]
    public ServerModelSelection DefaultModel { get; set; } = new(null, null);

    [JsonPropertyName("mcpSamplingTimeoutSeconds")]
    public int McpSamplingTimeoutSeconds { get; set; } = 30 * 60;

    [JsonPropertyName("chatHistoryMode")]
    public AppChatHistoryMode ChatHistoryMode { get; set; } = AppChatHistoryMode.None;

    [JsonPropertyName("embedding")]
    public EmbeddingSettings Embedding { get; set; } = new();

    [JsonPropertyName("stopAgentName")]
    public string StopAgentName { get; set; } = string.Empty;

    [JsonPropertyName("multiAgentSelectedAgents")]
    public List<string> MultiAgentSelectedAgents { get; set; } = [];
}
