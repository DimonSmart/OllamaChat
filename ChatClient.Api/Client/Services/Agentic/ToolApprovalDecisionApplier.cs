using ChatClient.Application.Services.Agentic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class ToolApprovalDecisionApplier
{
    public static AIContent Apply(
        ToolApprovalRequestContent request,
        string toolName,
        ToolApprovalDecision decision,
        SessionToolApprovalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(decision));

        if (decision == ToolApprovalDecision.ApproveForSession)
            policy.Grant(toolName);

        return decision switch
        {
            ToolApprovalDecision.ApproveOnce => request.CreateResponse(true, "User approved"),
            ToolApprovalDecision.ApproveForSession => request.CreateResponse(true, "User approved for this session"),
            ToolApprovalDecision.Deny => request.CreateResponse(false, "User denied"),
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };
    }
}
