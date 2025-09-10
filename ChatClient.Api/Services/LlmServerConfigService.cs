using ChatClient.Domain.Models;
using ChatClient.Application.Repositories;

namespace ChatClient.Api.Services;

public class LlmServerConfigService(ILlmServerConfigRepository repository) : ILlmServerConfigService
{
    private readonly ILlmServerConfigRepository _repository = repository;

    public Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync() => _repository.GetAllAsync();

    public async Task<LlmServerConfig?> GetByIdAsync(Guid id)
    {
        var servers = await _repository.GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task CreateAsync(LlmServerConfig serverConfig)
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        serverConfig.Id ??= Guid.NewGuid();
        serverConfig.CreatedAt = DateTime.UtcNow;
        serverConfig.UpdatedAt = DateTime.UtcNow;
        servers.Add(serverConfig);
        await _repository.SaveAllAsync(servers);
    }

    public async Task UpdateAsync(LlmServerConfig serverConfig)
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        var index = servers.FindIndex(s => s.Id == serverConfig.Id);
        if (index == -1)
            throw new KeyNotFoundException($"LLM server config with ID {serverConfig.Id} not found");
        serverConfig.UpdatedAt = DateTime.UtcNow;
        servers[index] = serverConfig;
        await _repository.SaveAllAsync(servers);
    }

    public async Task DeleteAsync(Guid id)
    {
        var servers = (await _repository.GetAllAsync()).ToList();
        var existing = servers.FirstOrDefault(s => s.Id == id) ??
                       throw new KeyNotFoundException($"LLM server config with ID {id} not found");
        servers.Remove(existing);
        await _repository.SaveAllAsync(servers);
    }
}

