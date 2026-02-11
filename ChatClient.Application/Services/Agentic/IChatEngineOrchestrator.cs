namespace ChatClient.Application.Services.Agentic;

public interface IChatEngineOrchestrator
{
    IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        ChatEngineOrchestrationRequest request,
        CancellationToken cancellationToken = default);
}
