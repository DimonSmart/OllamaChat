using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IRagFileService
{
    Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid agentId);
    Task<RagFile?> GetFileAsync(Guid agentId, string fileName);
    Task AddOrUpdateFileAsync(Guid agentId, RagFile file);
    Task DeleteFileAsync(Guid agentId, string fileName);
}

