using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class LlmServerConfigService : ILlmServerConfigService
{
    private readonly JsonFileRepository<List<LlmServerConfig>> _repository;

    public LlmServerConfigService(IConfiguration configuration, ILogger<LlmServerConfigService> logger)
    {
        var serversFilePath = configuration["LlmServers:FilePath"] ?? FilePathConstants.DefaultLlmServersFile;
        _repository = new JsonFileRepository<List<LlmServerConfig>>(serversFilePath, logger);
    }

    public async Task<List<LlmServerConfig>> GetAllAsync() => await _repository.ReadAsync() ?? [];

    public async Task<LlmServerConfig?> GetByIdAsync(Guid id)
    {
        var servers = await GetAllAsync();
        return servers.FirstOrDefault(s => s.Id == id);
    }

    public async Task CreateAsync(LlmServerConfig serverConfig)
    {
        await _repository.UpdateAsync(servers =>
        {
            serverConfig.Id ??= Guid.NewGuid();
            serverConfig.CreatedAt = DateTime.UtcNow;
            serverConfig.UpdatedAt = DateTime.UtcNow;

            servers.Add(serverConfig);
            return Task.CompletedTask;
        }, []);
    }

    public async Task UpdateAsync(LlmServerConfig serverConfig)
    {
        await _repository.UpdateAsync(servers =>
        {
            var index = servers.FindIndex(s => s.Id == serverConfig.Id);

            if (index == -1)
                throw new KeyNotFoundException($"LLM server config with ID {serverConfig.Id} not found");

            serverConfig.UpdatedAt = DateTime.UtcNow;
            servers[index] = serverConfig;

            return Task.CompletedTask;
        }, []);
    }

    public async Task DeleteAsync(Guid id)
    {
        await _repository.UpdateAsync(servers =>
        {
            var existing = servers.FirstOrDefault(s => s.Id == id) ??
                           throw new KeyNotFoundException($"LLM server config with ID {id} not found");

            servers.Remove(existing);
            return Task.CompletedTask;
        }, []);
    }
}
