using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Seed;

public class LlmServerConfigSeeder(ILlmServerConfigRepository repository)
{
    private readonly ILlmServerConfigRepository _repository = repository;

    public async Task SeedAsync()
    {
        var existing = await _repository.GetAllAsync();
        if (existing.Count > 0)
            return;

        var defaultServers = new List<LlmServerConfig>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Ollama",
                ServerType = ServerType.Ollama,
                BaseUrl = "http://localhost:11434",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        await _repository.SaveAllAsync(defaultServers);
    }
}

