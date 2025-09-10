namespace ChatClient.Application.Repositories;

using ChatClient.Domain.Models;

public interface IRagFileRepository
{
    Task<List<RagFile>> GetFilesAsync(Guid id);
    Task<RagFile?> GetFileAsync(Guid id, string fileName);
    Task AddOrUpdateFileAsync(Guid id, RagFile file);
    Task DeleteFileAsync(Guid id, string fileName);
}
