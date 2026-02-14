using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticExecutionRuntime
{
    IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgenticExecutionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}
