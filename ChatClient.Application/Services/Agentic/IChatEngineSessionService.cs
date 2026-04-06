using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineSessionService : IChatSessionService
{
    Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default);
}
