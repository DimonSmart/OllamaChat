using System.Collections.Generic;
using System.Threading.Tasks;

using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services;

/// <summary>
/// Class for managing streaming message state in chat
/// </summary>
public class StreamingMessageManager
{
    private readonly Func<IAppChatMessage, bool, Task>? _messageUpdatedCallback;
    private readonly Dictionary<Guid, StreamingAppChatMessage> _activeMessages = new();

    public StreamingMessageManager(Func<IAppChatMessage, bool, Task>? messageUpdatedCallback)
    {
        _messageUpdatedCallback = messageUpdatedCallback;
    }

    /// <summary>
    /// Creates a new streaming message and registers it for tracking
    /// </summary>
    public StreamingAppChatMessage CreateStreamingMessage(List<FunctionCallRecord>? functionCalls = null, string? agentName = null)
    {
        var message = new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant, functionCalls, agentName);
        _activeMessages[message.Id] = message;
        return message;
    }

    /// <summary>
    /// Appends content to an active streaming message and triggers update callback
    /// </summary>
    public async Task AppendToMessageAsync(Guid messageId, string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        if (_activeMessages.TryGetValue(messageId, out var message))
        {
            message.Append(content);
            if (_messageUpdatedCallback != null)
            {
                await _messageUpdatedCallback(message, false);
            }
        }
    }

    /// <summary>
    /// Completes streaming and returns final message
    /// </summary>
    public AppChatMessage CompleteStreaming(StreamingAppChatMessage streamingMessage, string? statistics = null)
    {
        if (!string.IsNullOrEmpty(statistics))
        {
            streamingMessage.SetStatistics(statistics);
        }
        _activeMessages.Remove(streamingMessage.Id);
        var finalMessage = new AppChatMessage(streamingMessage.Content, streamingMessage.MsgDateTime, ChatRole.Assistant, streamingMessage.Statistics, streamingMessage.Files, streamingMessage.FunctionCalls, streamingMessage.AgentName);
        finalMessage.Id = streamingMessage.Id; // Preserve the original ID
        finalMessage.IsCanceled = streamingMessage.IsCanceled;
        return finalMessage;
    }

    /// <summary>
    /// Cancels streaming message and returns a canceled message
    /// </summary>
    public AppChatMessage CancelStreaming(StreamingAppChatMessage streamingMessage)
    {
        streamingMessage.SetCanceled();
        _activeMessages.Remove(streamingMessage.Id);

        var finalMessage = new AppChatMessage(streamingMessage.Content, streamingMessage.MsgDateTime, ChatRole.Assistant, streamingMessage.Statistics, streamingMessage.Files, streamingMessage.FunctionCalls, streamingMessage.AgentName);
        finalMessage.Id = streamingMessage.Id; // Preserve the original ID
        finalMessage.IsCanceled = true;
        return finalMessage;
    }

    /// <summary>
    /// Creates statistics for message with additional metrics.
    /// </summary>
    public string BuildStatistics(TimeSpan processingTime, ChatConfiguration chatConfiguration, int tokenCount, IEnumerable<string>? invokedServers = null)
    {
        var tokensPerSecond = processingTime.TotalSeconds > 0
            ? (tokenCount / processingTime.TotalSeconds).ToString("F1")
            : "N/A";

        var statisticsBuilder = new System.Text.StringBuilder();
        statisticsBuilder.Append($"⏱️ {processingTime.TotalSeconds:F1}s");
        statisticsBuilder.Append($" • 🤖 {chatConfiguration.ModelName}");
        if (chatConfiguration.Functions.Any())
        {
            statisticsBuilder.Append($" • 🔧 {chatConfiguration.Functions.Count} funcs");
        }
        if (invokedServers != null && invokedServers.Any())
        {
            statisticsBuilder.Append($" • 🌐 {string.Join(", ", invokedServers)}");
        }
        statisticsBuilder.Append($" • 📊 {tokenCount} tokens ({tokensPerSecond}/s)");
        return statisticsBuilder.ToString();
    }
}
