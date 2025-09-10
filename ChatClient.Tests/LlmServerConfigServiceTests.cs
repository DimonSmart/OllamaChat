using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class LlmServerConfigServiceTests
{
    [Fact]
    public async Task CrudOperationsWork()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "[]");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LlmServers:FilePath"] = tempFile
                })
                .Build();

            var repoLogger = new LoggerFactory().CreateLogger<LlmServerConfigRepository>();
            var repository = new LlmServerConfigRepository(config, repoLogger);
            var service = new LlmServerConfigService(repository);

            var server = new LlmServerConfig
            {
                Name = "Test",
                ServerType = ServerType.Ollama,
                BaseUrl = "http://localhost"
            };

            await service.CreateAsync(server);
            Assert.NotNull(server.Id);

            var all = await service.GetAllAsync();
            Assert.Single(all);

            var retrieved = await service.GetByIdAsync(server.Id!.Value);
            Assert.NotNull(retrieved);
            Assert.Equal("Test", retrieved!.Name);

            server.Name = "Updated";
            await service.UpdateAsync(server);
            var updated = await service.GetByIdAsync(server.Id.Value);
            Assert.Equal("Updated", updated!.Name);

            await service.DeleteAsync(server.Id.Value);
            var afterDelete = await service.GetAllAsync();
            Assert.Empty(afterDelete);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
