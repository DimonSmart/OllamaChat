using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Helpers;
using System.Text.Json;

namespace ChatClient.Api.Services.Seed;

public class AgentDescriptionSeeder(
    IAgentDescriptionRepository repository,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<AgentDescriptionSeeder> logger)
{
    private readonly IAgentDescriptionRepository _repository = repository;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<AgentDescriptionSeeder> _logger = logger;

    public async Task SeedAsync()
    {
        var existing = await _repository.GetAllAsync();
        if (existing.Count > 0)
            return;

        var seedPath = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["AgentDescriptions:SeedFilePath"],
            "agent_descriptions.json");

        if (File.Exists(seedPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(seedPath);
                var seeded = JsonSerializer.Deserialize<List<AgentDescription>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (seeded is { Count: > 0 })
                {
                    await _repository.SaveAllAsync(seeded);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed agent descriptions from {SeedPath}", seedPath);
            }
        }

        var fallbackAgents = new List<AgentDescription>
        {
            new()
            {
                AgentName = "Default Assistant",
                Content = "You are a helpful assistant.",
            },
            new()
            {
                AgentName = "Code Assistant",
                Content = "You are a coding assistant. Help the user write and understand code.",
            }
        };

        await _repository.SaveAllAsync(fallbackAgents);
    }
}

