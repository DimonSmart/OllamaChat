using ChatClient.Domain.Models;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticExecutionRuntime
{
    IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
