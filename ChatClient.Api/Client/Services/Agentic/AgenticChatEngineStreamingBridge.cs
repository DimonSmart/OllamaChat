using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatEngineStreamingBridge : IChatEngineStreamingBridge
{
    public StreamingAppChatMessage Create(string? agentId, string? agentName) =>
        new(string.Empty, DateTime.Now, AppChatRole.Assistant, null, agentId, agentName);

    public void Append(StreamingAppChatMessage message, string content)
    {
        if (message is null || string.IsNullOrEmpty(content))
            return;

        message.Append(content);
        message.ApproximateTokenCount++;
    }

    public AppChatMessage Complete(StreamingAppChatMessage message, string? statistics = null)
    {
        return Complete(message, message.Content, statistics);
    }

    public AppChatMessage Complete(StreamingAppChatMessage message, string finalContent, string? statistics = null)
    {
        if (!string.IsNullOrEmpty(statistics))
        {
            message.SetStatistics(statistics);
        }

        var finalMessage = new AppChatMessage(
            finalContent,
            message.MsgDateTime,
            AppChatRole.Assistant,
            message.Statistics,
            message.Files,
            message.ToolInvocations,
            message.AgentId,
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
            AppChatRole.Assistant,
            message.Statistics,
            message.Files,
            message.ToolInvocations,
            message.AgentId,
            message.AgentName)
        {
            Id = message.Id,
            IsCanceled = true
        };
    }
}
