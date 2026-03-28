using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IHandoffWorkflowSessionService : IChatEngineSessionService
{
    string? TaskSessionId { get; }

    Task StartAsync(HandoffWorkflowSessionStartRequest request, CancellationToken cancellationToken = default);
}
