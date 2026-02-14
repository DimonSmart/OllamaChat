using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineHistoryBuilder
{
    IReadOnlyList<IAppChatMessage> Build(IEnumerable<IAppChatMessage> messages);
}
