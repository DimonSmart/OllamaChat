using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class SemanticKernelChatEngineOrchestrator : IChatEngineOrchestrator
{
    public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        ChatEngineOrchestrationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return new ChatEngineStreamChunk(
            request.Agent.AgentName,
            "SemanticKernel orchestration is served by the existing runtime adapter.",
            IsFinal: true,
            IsError: true);
    }
}
