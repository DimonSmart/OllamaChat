using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineSessionService : IChatSessionService
{
    event Action? SessionStateChanged;

    Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default);

    Task<AgentSessionStateViewModel?> GetSessionStateAsync(CancellationToken cancellationToken = default);
}
