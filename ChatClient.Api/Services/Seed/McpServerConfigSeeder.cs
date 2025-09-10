using ChatClient.Domain.Models;
using ChatClient.Application.Repositories;

namespace ChatClient.Api.Services;

public class McpServerConfigSeeder(IMcpServerConfigRepository repository)
{
    private readonly IMcpServerConfigRepository _repository = repository;

    public async Task SeedAsync()
    {
        var existing = await _repository.GetAllAsync();
        if (existing.Count > 0)
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

        await _repository.SaveAllAsync(defaultServers);
    }
}

