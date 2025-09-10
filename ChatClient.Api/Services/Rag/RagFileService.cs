using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public class RagFileService(IRagFileRepository repository, IRagVectorIndexBackgroundService indexBackgroundService) : IRagFileService
{
    private readonly IRagFileRepository _repository = repository;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService = indexBackgroundService;

    public Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid id) => _repository.GetFilesAsync(id);

    public Task<RagFile?> GetFileAsync(Guid id, string fileName)
    {
        FileNameValidator.Validate(fileName);
        return _repository.GetFileAsync(id, fileName);
    }

    public async Task AddOrUpdateFileAsync(Guid id, RagFile file)
    {
        FileNameValidator.Validate(file.FileName);
        await _repository.AddOrUpdateFileAsync(id, file);
        _indexBackgroundService.RequestRebuild();
    }

    public async Task DeleteFileAsync(Guid id, string fileName)
    {
        FileNameValidator.Validate(fileName);
        await _repository.DeleteFileAsync(id, fileName);
        _indexBackgroundService.RequestRebuild();
    }
}
