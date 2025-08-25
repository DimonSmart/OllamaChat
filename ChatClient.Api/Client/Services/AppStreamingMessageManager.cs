using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services;

/// <summary>
/// Manages streaming message state in chat.
/// </summary>
public class AppStreamingMessageManager
{
    public AppStreamingMessageManager()
    {
    }

    /// <summary>
    /// Creates a new streaming message
    /// </summary>
    public StreamingAppChatMessage CreateStreamingMessage(List<FunctionCallRecord>? functionCalls = null, string? agentName = null)
    {
        return new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant, functionCalls, agentName);
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

        var finalMessage = new AppChatMessage(streamingMessage.Content, streamingMessage.MsgDateTime, ChatRole.Assistant, streamingMessage.Statistics, streamingMessage.Files, streamingMessage.FunctionCalls, streamingMessage.AgentName);
        finalMessage.Id = streamingMessage.Id; // Preserve the original ID
        finalMessage.IsCanceled = true;
        return finalMessage;
    }

    /// <summary>
    /// Creates statistics for message with additional metrics.
    /// </summary>
    public string BuildStatistics(TimeSpan processingTime, string modelName, int tokenCount, int functionCount, IEnumerable<string>? invokedServers = null)
    {
        var tokensPerSecond = processingTime.TotalSeconds > 0
            ? (tokenCount / processingTime.TotalSeconds).ToString("F1")
            : "N/A";

        var statisticsBuilder = new System.Text.StringBuilder();
        statisticsBuilder.Append($"⏱️ {processingTime.TotalSeconds:F1}s");
        statisticsBuilder.Append($" • 🤖 {modelName}");
        if (functionCount > 0)
        {
            statisticsBuilder.Append($" • 🔧 {functionCount} funcs");
        }
        if (invokedServers != null && invokedServers.Any())
        {
            statisticsBuilder.Append($" • 🌐 {string.Join(", ", invokedServers)}");
        }
        statisticsBuilder.Append($" • 📊 {tokenCount} tokens ({tokensPerSecond}/s)");
        return statisticsBuilder.ToString();
    }
}
