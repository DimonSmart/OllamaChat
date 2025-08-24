using ChatClient.Api.Services;
using ChatClient.Shared.Models;

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

            var logger = new LoggerFactory().CreateLogger<LlmServerConfigService>();
            var service = new LlmServerConfigService(config, logger);

            var server = new LlmServerConfig
            {
                Name = "Test",
                ServerType = ServerType.Ollama,
                BaseUrl = "http://localhost"
            };

            var created = await service.CreateAsync(server);
            Assert.NotNull(created.Id);

            var all = await service.GetAllAsync();
            Assert.Single(all);

            var retrieved = await service.GetByIdAsync(created.Id!.Value);
            Assert.NotNull(retrieved);
            Assert.Equal("Test", retrieved!.Name);

            created.Name = "Updated";
            await service.UpdateAsync(created);
            var updated = await service.GetByIdAsync(created.Id.Value);
            Assert.Equal("Updated", updated!.Name);

            await service.DeleteAsync(created.Id.Value);
            var afterDelete = await service.GetAllAsync();
            Assert.Empty(afterDelete);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
