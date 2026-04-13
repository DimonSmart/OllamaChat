using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public interface IAgenticExecutionRuntime
{
    IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
