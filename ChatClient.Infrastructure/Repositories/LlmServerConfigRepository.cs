using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class LlmServerConfigRepository : ILlmServerConfigRepository
{
    private readonly JsonFileRepository<List<LlmServerConfig>> _repo;

    public LlmServerConfigRepository(IConfiguration configuration, ILogger<LlmServerConfigRepository> logger)
    {
        var filePath = configuration["LlmServers:FilePath"] ?? FilePathConstants.DefaultLlmServersFile;
        _repo = new JsonFileRepository<List<LlmServerConfig>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repo.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<LlmServerConfig> servers, CancellationToken cancellationToken = default) =>
        _repo.WriteAsync(servers, cancellationToken);
}

