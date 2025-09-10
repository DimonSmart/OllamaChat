using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class McpServerConfigRepository : IMcpServerConfigRepository
{
    private readonly JsonFileRepository<List<McpServerConfig>> _repo;

    public McpServerConfigRepository(IConfiguration configuration, ILogger<McpServerConfigRepository> logger)
    {
        var filePath = configuration["McpServers:FilePath"] ?? FilePathConstants.DefaultMcpServersFile;
        _repo = new JsonFileRepository<List<McpServerConfig>>(filePath, logger);
    }

    public async Task<IReadOnlyCollection<McpServerConfig>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _repo.ReadAsync(cancellationToken) ?? [];

    public Task SaveAllAsync(List<McpServerConfig> servers, CancellationToken cancellationToken = default) =>
        _repo.WriteAsync(servers, cancellationToken);
}

