using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public class RagFileService(
    IRagFileRepository repository,
    IRagVectorIndexBackgroundService indexBackgroundService,
    IRagVectorStore vectorStore) : IRagFileService
{
    private readonly IRagFileRepository _repository = repository;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService = indexBackgroundService;
    private readonly IRagVectorStore _vectorStore = vectorStore;

    public async Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid agentId)
    {
        var files = await _repository.GetFilesAsync(agentId);
        foreach (var f in files)
        {
            f.HasIndex = await _vectorStore.HasFileAsync(agentId, f.FileName);
        }
        return files;
    }

    public async Task<RagFile?> GetFileAsync(Guid agentId, string fileName)
    {
        FileNameValidator.Validate(fileName);
        var file = await _repository.GetFileAsync(agentId, fileName);
        if (file is null)
            return null;
        file.HasIndex = await _vectorStore.HasFileAsync(agentId, fileName);
        return file;
    }

    public async Task AddOrUpdateFileAsync(Guid agentId, RagFile file)
    {
        FileNameValidator.Validate(file.FileName);
        await _repository.AddOrUpdateFileAsync(agentId, file);
        await _vectorStore.RemoveFileAsync(agentId, file.FileName);
        _indexBackgroundService.RequestRebuild();
    }

    public async Task DeleteFileAsync(Guid agentId, string fileName)
    {
        FileNameValidator.Validate(fileName);
        await _repository.DeleteFileAsync(agentId, fileName);
        await _vectorStore.RemoveFileAsync(agentId, fileName);
        _indexBackgroundService.RequestRebuild();
    }
}
