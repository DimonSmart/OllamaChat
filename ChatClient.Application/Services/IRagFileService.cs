using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IRagFileService
{
    Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid id);
    Task<RagFile?> GetFileAsync(Guid id, string fileName);
    Task AddOrUpdateFileAsync(Guid id, RagFile file);
    Task DeleteFileAsync(Guid id, string fileName);
}

