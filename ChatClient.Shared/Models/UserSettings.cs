using System;
using System.Collections.Generic;
using System.Text.Json;
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
    public ServerModel DefaultModel { get; set; } = new(Guid.Empty, string.Empty);

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
    [JsonPropertyName("embeddingModel")]
    public ServerModel EmbeddingModel { get; set; } = new(Guid.Empty, string.Empty);

    [JsonPropertyName("ragLineChunkSize")]
    public int RagLineChunkSize { get; set; } = 256;

    [JsonPropertyName("ragParagraphChunkSize")]
    public int RagParagraphChunkSize { get; set; } = 512;

    [JsonPropertyName("ragParagraphOverlap")]
    public int RagParagraphOverlap { get; set; } = 64;

    [JsonPropertyName("stopAgentName")]
    public string StopAgentName { get; set; } = string.Empty;

    [JsonPropertyName("multiAgentSelectedAgents")]
    public List<string> MultiAgentSelectedAgents { get; set; } = [];

    [JsonPropertyName("stopAgentOptions")]
    public JsonElement? StopAgentOptions { get; set; }
}
