using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public class LlmServerConfigSeeder
{
    private readonly JsonFileRepository<List<LlmServerConfig>> _repository;

    public LlmServerConfigSeeder(IConfiguration configuration, ILogger<LlmServerConfigSeeder> logger)
    {
        var filePath = configuration["LlmServers:FilePath"] ?? FilePathConstants.DefaultLlmServersFile;
        _repository = new JsonFileRepository<List<LlmServerConfig>>(filePath, logger);
    }

    public async Task SeedAsync()
    {
        if (_repository.Exists)
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

        await _repository.WriteAsync(defaultServers);
    }
}
