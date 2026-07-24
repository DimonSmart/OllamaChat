using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class TodoProviderProfileServiceTests
{
    [Fact]
    public async Task CrudOperations_PersistProfilesAndPreserveCreationTimestamp()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]");
        try
        {
            var service = CreateService(tempFile);
            var profile = new TodoProviderProfile
            {
                Id = Guid.Empty,
                Name = "  Software Development  ",
                Instructions = "\nPlan work carefully.\n",
                TodoListMessageTemplate = "### Current work plan\n\n{todos}\n"
            };

            await service.CreateAsync(profile);

            Assert.NotEqual(Guid.Empty, profile.Id);
            Assert.Equal("Software Development", profile.Name);
            Assert.NotEqual(default, profile.CreatedAt);
            Assert.Equal(profile.CreatedAt, profile.UpdatedAt);

            var createdAt = profile.CreatedAt;
            profile.Instructions = "Updated instructions";
            await service.UpdateAsync(profile);

            var updated = await service.GetByIdAsync(profile.Id);
            Assert.NotNull(updated);
            Assert.Equal(createdAt, updated!.CreatedAt);
            Assert.True(updated.UpdatedAt >= createdAt);
            Assert.Equal("Updated instructions", updated.Instructions);
            Assert.Equal("### Current work plan\n\n{todos}", updated.TodoListMessageTemplate);

            await service.DeleteAsync(profile.Id);
            Assert.Empty(await service.GetAllAsync());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CreateAsync_RejectsEmptyAndCaseInsensitiveDuplicateNames_AndAllowsEmptyOptionalText()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]");
        try
        {
            var service = CreateService(tempFile);

            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new TodoProviderProfile { Name = "  " }));

            var profile = new TodoProviderProfile
            {
                Name = "Research",
                Instructions = " \t ",
                TodoListMessageTemplate = "\r\n"
            };
            await service.CreateAsync(profile);

            Assert.Null(profile.Instructions);
            Assert.Null(profile.TodoListMessageTemplate);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new TodoProviderProfile { Name = "research" }));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static TodoProviderProfileService CreateService(string filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TodoProviderProfiles:FilePath"] = filePath })
            .Build();
        var logger = new LoggerFactory().CreateLogger<TodoProviderProfileRepository>();
        return new TodoProviderProfileService(new TodoProviderProfileRepository(configuration, logger));
    }
}
