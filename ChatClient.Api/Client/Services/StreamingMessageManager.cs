using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services;

/// <summary>
/// Class for managing streaming message state in chat
/// </summary>
public class StreamingMessageManager
{
    private readonly Func<IAppChatMessage, Task>? _messageUpdatedCallback;

    public StreamingMessageManager(Func<IAppChatMessage, Task>? messageUpdatedCallback)
    {
        _messageUpdatedCallback = messageUpdatedCallback;
    }

    /// <summary>
    /// Creates a new streaming message
    /// </summary>
    public StreamingAppChatMessage CreateStreamingMessage()
    {
        return new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant);
    }

    /// <summary>
    /// Updates streaming message content
    /// </summary>
    public async Task UpdateStreamingContentAsync(StreamingAppChatMessage message, string content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            message.Append(content);
            if (_messageUpdatedCallback != null)
            {
                await _messageUpdatedCallback(message);
            }
        }
    }    /// <summary>
         /// Completes streaming and returns final message
         /// </summary>
    public AppChatMessage CompleteStreaming(StreamingAppChatMessage streamingMessage, string? statistics = null)
    {
        if (!string.IsNullOrEmpty(statistics))
        {
            streamingMessage.SetStatistics(statistics);
        }
        var finalMessage = new AppChatMessage(streamingMessage.Content, streamingMessage.MsgDateTime, ChatRole.Assistant, streamingMessage.Statistics);
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

        var finalMessage = new AppChatMessage(streamingMessage.Content, streamingMessage.MsgDateTime, ChatRole.Assistant, streamingMessage.Statistics);
        finalMessage.Id = streamingMessage.Id; // Preserve the original ID
        finalMessage.IsCanceled = true;
        return finalMessage;
    }

    /// <summary>
    /// Creates statistics for message with additional metrics
    /// </summary>
    public string BuildStatistics(TimeSpan processingTime, string modelName, IReadOnlyCollection<string>? functionNames, int? tokenCount = null)
    {
        var functionsText = functionNames?.Any() == true ? string.Join(", ", functionNames) : "None";
        var tokensPerSecond = tokenCount.HasValue && processingTime.TotalSeconds > 0
            ? (tokenCount.Value / processingTime.TotalSeconds).ToString("F1")
            : "N/A";

        var statisticsBuilder = new System.Text.StringBuilder();
        statisticsBuilder.AppendLine("\n\n---");
        statisticsBuilder.AppendLine($"⏱️ Processing time: {processingTime.TotalSeconds:F2} seconds");
        statisticsBuilder.AppendLine($"🤖 Model: {modelName}");
        statisticsBuilder.AppendLine($"🔧 Functions: {functionsText}");

        if (tokenCount.HasValue)
        {
            statisticsBuilder.AppendLine($"📊 Tokens: {tokenCount.Value} (~{tokensPerSecond} tokens/sec)");
        }
        return statisticsBuilder.ToString();
    }
}