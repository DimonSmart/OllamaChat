using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatEngineStreamingBridge : IChatEngineStreamingBridge
{
    public StreamingAppChatMessage Create(string agentName) =>
        new(string.Empty, DateTime.Now, ChatRole.Assistant, null, agentName);

    public void Append(StreamingAppChatMessage message, string content)
    {
        if (message is null || string.IsNullOrEmpty(content))
            return;

        message.Append(content);
        message.ApproximateTokenCount++;
    }

    public AppChatMessage Complete(StreamingAppChatMessage message, string? statistics = null)
    {
        if (!string.IsNullOrEmpty(statistics))
        {
            message.SetStatistics(statistics);
        }

        var finalMessage = new AppChatMessage(
            message.Content,
            message.MsgDateTime,
            ChatRole.Assistant,
            message.Statistics,
            message.Files,
            message.FunctionCalls,
            message.AgentName)
        {
            Id = message.Id,
            IsCanceled = message.IsCanceled
        };
        return finalMessage;
    }

    public AppChatMessage Cancel(StreamingAppChatMessage message)
    {
        message.SetCanceled();

        return new AppChatMessage(
            message.Content,
            message.MsgDateTime,
            ChatRole.Assistant,
            message.Statistics,
            message.Files,
            message.FunctionCalls,
            message.AgentName)
        {
            Id = message.Id,
            IsCanceled = true
        };
    }
}
