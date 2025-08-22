using ChatClient.Shared.Models;

namespace ChatClient.Shared.Services;

public interface IRagFileService
{
    Task<List<RagFile>> GetFilesAsync(Guid id);
    Task<RagFile?> GetFileAsync(Guid id, string fileName);
    Task AddOrUpdateFileAsync(Guid id, RagFile file);
    Task DeleteFileAsync(Guid id, string fileName);
}

