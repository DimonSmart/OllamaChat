using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IOrchestrationWorkflowSessionService : IEditableChatSessionService
{
    string? TaskSessionId { get; }

    Task StartAsync(OrchestrationWorkflowSessionStartRequest request, CancellationToken cancellationToken = default);

    Task KickoffAsync(CancellationToken cancellationToken = default);
}
