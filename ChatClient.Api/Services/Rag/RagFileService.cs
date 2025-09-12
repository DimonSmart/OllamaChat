using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.SemanticKernel.Memory;

namespace ChatClient.Api.Services.Rag;

public class RagFileService(IRagFileRepository repository, IRagVectorIndexBackgroundService indexBackgroundService, IMemoryStore memoryStore) : IRagFileService
{
    private readonly IRagFileRepository _repository = repository;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService = indexBackgroundService;
    private readonly IMemoryStore _memoryStore = memoryStore;

    public async Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid agentId)
    {
        var files = await _repository.GetFilesAsync(agentId);
        var collection = CollectionName(agentId);
        await _memoryStore.CreateCollectionAsync(collection, cancellationToken: default);
        foreach (var f in files)
        {
            var key = Key(f.FileName, 0);
            f.HasIndex = await _memoryStore.GetAsync(collection, key, withEmbedding: false, cancellationToken: default) is not null;
        }
        return files;
    }

    public async Task<RagFile?> GetFileAsync(Guid agentId, string fileName)
    {
        FileNameValidator.Validate(fileName);
        var file = await _repository.GetFileAsync(agentId, fileName);
        if (file is null)
            return null;
        var collection = CollectionName(agentId);
        await _memoryStore.CreateCollectionAsync(collection, cancellationToken: default);
        var key = Key(fileName, 0);
        file.HasIndex = await _memoryStore.GetAsync(collection, key, withEmbedding: false, cancellationToken: default) is not null;
        return file;
    }

    public async Task AddOrUpdateFileAsync(Guid agentId, RagFile file)
    {
        FileNameValidator.Validate(file.FileName);
        await _repository.AddOrUpdateFileAsync(agentId, file);
        await RemoveEmbeddingsAsync(agentId, file.FileName);
        _indexBackgroundService.RequestRebuild();
    }

    public async Task DeleteFileAsync(Guid agentId, string fileName)
    {
        FileNameValidator.Validate(fileName);
        await _repository.DeleteFileAsync(agentId, fileName);
        await RemoveEmbeddingsAsync(agentId, fileName);
        _indexBackgroundService.RequestRebuild();
    }

    private async Task RemoveEmbeddingsAsync(Guid agentId, string fileName)
    {
        var collection = CollectionName(agentId);
        await _memoryStore.CreateCollectionAsync(collection, cancellationToken: default);
        for (var i = 0; ; i++)
        {
            var key = Key(fileName, i);
            var record = await _memoryStore.GetAsync(collection, key, cancellationToken: default);
            if (record is null)
                break;
            await _memoryStore.RemoveAsync(collection, key, cancellationToken: default);
        }
    }

    private static string CollectionName(Guid agentId) => $"agent_{agentId:N}";
    private static string Key(string file, int index) => $"{file}#{index:D5}";
}
