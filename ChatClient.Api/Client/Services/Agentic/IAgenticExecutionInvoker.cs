namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticExecutionInvoker
{
    Task<AgenticExecutionInvocationResult> InvokeAsync(
        AgenticExecutionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}
