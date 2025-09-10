using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public class UserSettingsRepositoryTests
{
    [Fact]
    public async Task SaveAndGetAsync_PersistsData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UserSettings:FilePath"] = path
                })
                .Build();
            var repo = new UserSettingsRepository(config, NullLogger<UserSettingsRepository>.Instance);
            var settings = new UserSettings { UserName = "Alice" };
            await repo.SaveAsync(settings);
            Assert.True(repo.Exists);
            var loaded = await repo.GetAsync();
            Assert.NotNull(loaded);
            Assert.Equal("Alice", loaded!.UserName);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

