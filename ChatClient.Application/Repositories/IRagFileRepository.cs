namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IRagFileRepository
{
    Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid agentId);
    Task<RagFile?> GetFileAsync(Guid agentId, string fileName);
    Task AddOrUpdateFileAsync(Guid agentId, RagFile file);
    Task DeleteFileAsync(Guid agentId, string fileName);
}
