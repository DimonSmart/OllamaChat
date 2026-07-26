using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineSessionService : IChatSessionService
{
    event Action? SessionStateChanged;

    ToolApprovalRequestViewModel? PendingToolApproval { get; }

    Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default);

    Task<AgentSessionStateViewModel?> GetSessionStateAsync(CancellationToken cancellationToken = default);

    Task SetAgentModeAsync(string mode, CancellationToken cancellationToken = default);

    Task SetFileAccessWorkspaceAsync(string workspace, CancellationToken cancellationToken = default);

    Task RespondToToolApprovalAsync(ToolApprovalDecision decision, CancellationToken cancellationToken = default);
}
