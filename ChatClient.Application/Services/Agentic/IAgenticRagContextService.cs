namespace ChatClient.Application.Services.Agentic;

public interface IAgenticRagContextService
{
    Task<AgenticRagContextResult> TryBuildContextAsync(
        Guid agentId,
        string query,
        Guid? serverId = null,
        CancellationToken cancellationToken = default);
}
