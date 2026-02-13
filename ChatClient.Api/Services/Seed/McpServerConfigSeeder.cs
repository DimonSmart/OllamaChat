using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using System.Text.Json;

namespace ChatClient.Api.Services.Seed;

public class McpServerConfigSeeder(
    IMcpServerConfigRepository repository,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<McpServerConfigSeeder> logger)
{
    private readonly IMcpServerConfigRepository _repository = repository;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<McpServerConfigSeeder> _logger = logger;

    public async Task SeedAsync()
    {
        var existing = await _repository.GetAllAsync();
        if (existing.Count > 0)
            return;

        var seedPath = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["McpServers:SeedFilePath"],
            "mcp_servers.json");

        if (File.Exists(seedPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(seedPath);
                var seeded = JsonSerializer.Deserialize<List<McpServerConfig>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (seeded is { Count: > 0 })
                {
                    await _repository.SaveAllAsync(seeded);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed MCP servers from {SeedPath}", seedPath);
            }
        }

        var fallbackServers = new List<McpServerConfig>
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

        await _repository.SaveAllAsync(fallbackServers);
    }
}

