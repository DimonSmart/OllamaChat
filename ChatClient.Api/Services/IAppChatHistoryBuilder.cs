using ChatClient.Shared.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public interface IAppChatHistoryBuilder
{
    Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, Guid agentId, CancellationToken cancellationToken, Guid? serverId = null);
}
