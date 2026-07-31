namespace ChatClient.Application.Services.Agentic;

public sealed record SessionToolApprovalRequest(
    string RequestId,
    string ToolName,
    string Arguments,
    ToolApprovalSessionScope SessionScope,
    string? WorkspacePath);

public interface ISessionToolApprovalCoordinator
{
    SessionToolApprovalRequest? PendingRequest { get; }

    event Action? PendingRequestChanged;

    Task<ToolApprovalDecision> RequestApprovalAsync(
        SessionToolApprovalRequest request,
        CancellationToken cancellationToken = default);

    bool TryRespond(
        string requestId,
        ToolApprovalDecision decision);

    void CancelPending();
}
