using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticExecutionInvoker
{
    Task<AgenticExecutionInvocationResult> InvokeAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
