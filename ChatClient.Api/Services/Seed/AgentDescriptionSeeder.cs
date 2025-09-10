using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class AgentDescriptionSeeder
{
    private readonly JsonFileRepository<List<AgentDescription>> _repository;

    public AgentDescriptionSeeder(IConfiguration configuration, ILogger<AgentDescriptionSeeder> logger)
    {
        var filePath = configuration["AgentDescriptions:FilePath"] ?? FilePathConstants.DefaultAgentDescriptionsFile;
        _repository = new JsonFileRepository<List<AgentDescription>>(filePath, logger);
    }

    public async Task SeedAsync()
    {
        if (_repository.Exists)
            return;

        var defaultAgents = new List<AgentDescription>
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

        await _repository.WriteAsync(defaultAgents);
    }
}
