using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public class RagFileService(IRagFileRepository repository, IRagVectorIndexBackgroundService indexBackgroundService) : IRagFileService
{
    private readonly IRagFileRepository _repository = repository;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService = indexBackgroundService;

    public Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid agentId) => _repository.GetFilesAsync(agentId);

    public Task<RagFile?> GetFileAsync(Guid agentId, string fileName)
    {
        FileNameValidator.Validate(fileName);
        return _repository.GetFileAsync(agentId, fileName);
    }

    public async Task AddOrUpdateFileAsync(Guid agentId, RagFile file)
    {
        FileNameValidator.Validate(file.FileName);
        await _repository.AddOrUpdateFileAsync(agentId, file);
        _indexBackgroundService.RequestRebuild();
    }

    public async Task DeleteFileAsync(Guid agentId, string fileName)
    {
        FileNameValidator.Validate(fileName);
        await _repository.DeleteFileAsync(agentId, fileName);
        _indexBackgroundService.RequestRebuild();
    }
}
