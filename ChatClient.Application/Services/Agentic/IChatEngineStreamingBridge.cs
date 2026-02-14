using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineStreamingBridge
{
    StreamingAppChatMessage Create(string agentName);

    void Append(StreamingAppChatMessage message, string content);

    AppChatMessage Complete(StreamingAppChatMessage message, string? statistics = null);

    AppChatMessage Cancel(StreamingAppChatMessage message);
}
