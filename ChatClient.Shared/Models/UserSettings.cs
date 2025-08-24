using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using ChatClient.Shared.Constants;

namespace ChatClient.Shared.Models;

public class UserSettings
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("defaultModelName")]
    public string DefaultModelName { get; set; } = string.Empty;

    [JsonPropertyName("defaultChatMessage")]
    public string DefaultChatMessage { get; set; } = string.Empty;

    [JsonPropertyName("defaultMultiAgentChatMessage")]
    public string DefaultMultiAgentChatMessage { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("agentName")]
    public string AgentName { get; set; } = string.Empty;

    [JsonPropertyName("llms")]
    public List<LlmServerConfig> Llms { get; set; } = [];

    [JsonPropertyName("defaultLlmId")]
    public Guid? DefaultLlmId { get; set; }

    /// <summary>
    /// HTTP request timeout in seconds for MCP sampling requests (typically longer than regular API calls)
    /// </summary>
    [JsonPropertyName("mcpSamplingTimeoutSeconds")]
    public int McpSamplingTimeoutSeconds { get; set; } = 30 * 60;

    /// <summary>
    /// Number of functions to auto-select for new chats. Set to 0 to disable
    /// auto-selection.
    /// </summary>
    [JsonPropertyName("defaultAutoSelectCount")]
    public int DefaultAutoSelectCount { get; set; } = 0;

    /// <summary>
    /// Defines how chat history should be prepared before sending to the LLM
    /// </summary>
    [JsonPropertyName("chatHistoryMode")]
    public AppChatHistoryMode ChatHistoryMode { get; set; } = AppChatHistoryMode.None;

    /// <summary>
    /// Embedding model used for building the function index
    /// </summary>
    [JsonPropertyName("embeddingModelName")]
    public string EmbeddingModelName { get; set; } = string.Empty;

    [JsonPropertyName("stopAgentName")]
    public string StopAgentName { get; set; } = string.Empty;

    [JsonPropertyName("multiAgentSelectedAgents")]
    public List<string> MultiAgentSelectedAgents { get; set; } = [];

    [JsonPropertyName("stopAgentOptions")]
    public JsonElement? StopAgentOptions { get; set; }
}
