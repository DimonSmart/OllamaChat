using ChatClient.Domain.Models;
using ChatClient.Application.Repositories;

namespace ChatClient.Api.Services;

public class AgentDescriptionSeeder(IAgentDescriptionRepository repository)
{
    private readonly IAgentDescriptionRepository _repository = repository;

    public async Task SeedAsync()
    {
        var existing = await _repository.GetAllAsync();
        if (existing.Count > 0)
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

        await _repository.SaveAllAsync(defaultAgents);
    }
}

