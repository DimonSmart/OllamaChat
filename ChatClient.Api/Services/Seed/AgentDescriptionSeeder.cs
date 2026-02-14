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
                Content = "You are a polite and helpful assistant.\n\nMandatory personalization rule for EVERY user message:\n1) Before writing any final answer, first call MCP tool `prefs_get` with key `displayName` (aliases: `name`, `preferred_name`).\n2) If the name is missing, use elicitation to ask the user and save it.\n3) Then answer and address the user by name naturally at least once in the first sentence.\n\nNever skip this lookup, even for very simple questions (for example: current time). If lookup fails, continue politely without using a name.",
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

