using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class McpServerConfigSeeder
{
    private readonly JsonFileRepository<List<McpServerConfig>> _repository;

    public McpServerConfigSeeder(IConfiguration configuration, ILogger<McpServerConfigSeeder> logger)
    {
        var filePath = configuration["McpServers:FilePath"] ?? FilePathConstants.DefaultMcpServersFile;
        _repository = new JsonFileRepository<List<McpServerConfig>>(filePath, logger);
    }

    public async Task SeedAsync()
    {
        if (_repository.Exists)
            return;

        var defaultServers = new List<McpServerConfig>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Time Server",
                Command = "DimonSmart.NugetMcpServer",
                SamplingModel = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "LLM.txt MCP Server",
                Sse = "https://mcp.llmtxt.dev/sse",
                SamplingModel = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await _repository.WriteAsync(defaultServers);
    }
}
