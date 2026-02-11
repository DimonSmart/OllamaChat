using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class SemanticKernelChatEngineStreamingBridge : IChatEngineStreamingBridge
{
    private readonly AppStreamingMessageManager _streamingMessageManager = new();

    public StreamingAppChatMessage Create(string agentName) =>
        new(string.Empty, DateTime.Now, ChatRole.Assistant, null, agentName);

    public void Append(StreamingAppChatMessage message, string content)
    {
        if (message is null || string.IsNullOrEmpty(content))
            return;

        message.Append(content);
        message.ApproximateTokenCount++;
    }

    public AppChatMessage Complete(StreamingAppChatMessage message, string? statistics = null) =>
        _streamingMessageManager.CompleteStreaming(message, statistics);

    public AppChatMessage Cancel(StreamingAppChatMessage message) =>
        _streamingMessageManager.CancelStreaming(message);
}
