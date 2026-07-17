using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineSessionService : IChatSessionService
{
    ChatEngineSessionStartRequest? CurrentStartRequest { get; }

    Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default);
}
