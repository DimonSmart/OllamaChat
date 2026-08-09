using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class AgentModeProviderProfileServiceTests
{
    [Fact]
    public async Task CrudOperations_PersistModesAndPreserveCreationTimestamp()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]", cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var service = CreateService(tempFile);
            var profile = CreateValidProfile("  Plan / Execute  ");
            profile.Instructions = "Modes:\n{available_modes}\nCurrent: {current_mode}";
            profile.Modes.Add(new AgentModeProfile { Name = "review", Instructions = "Review the completed work.\nReport risks." });

            await service.CreateAsync(profile);

            Assert.NotEqual(Guid.Empty, profile.Id);
            Assert.Equal("Plan / Execute", profile.Name);
            Assert.Equal(profile.CreatedAt, profile.UpdatedAt);
            var createdAt = profile.CreatedAt;

            profile.Modes[0].Instructions = "Create a plan.\nAsk clarifying questions.";
            await service.UpdateAsync(profile);

            var updated = await service.GetByIdAsync(profile.Id);
            Assert.NotNull(updated);
            Assert.Equal(createdAt, updated!.CreatedAt);
            Assert.True(updated.UpdatedAt >= createdAt);
            Assert.Equal(new[] { "plan", "execute", "review" }, updated.Modes.Select(mode => mode.Name));
            Assert.Equal("Create a plan.\nAsk clarifying questions.", updated.Modes[0].Instructions);
            Assert.Equal("Modes:\n{available_modes}\nCurrent: {current_mode}", updated.Instructions);

            await service.DeleteAsync(profile.Id);
            Assert.Empty(await service.GetAllAsync());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CreateAsync_ValidatesProfileModesDefaultAndInstructions()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "[]", cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var service = CreateService(tempFile);

            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new AgentModeProviderProfile { Name = " " }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new AgentModeProviderProfile { Name = "Empty modes", DefaultMode = "plan" }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new AgentModeProviderProfile
            {
                Name = "Missing mode instructions",
                DefaultMode = "plan",
                Modes = [new() { Name = "plan" }]
            }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new AgentModeProviderProfile
            {
                Name = "Duplicate modes",
                DefaultMode = "plan",
                Modes = [new() { Name = "plan", Instructions = "A" }, new() { Name = "PLAN", Instructions = "B" }]
            }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new AgentModeProviderProfile
            {
                Name = "Missing default",
                DefaultMode = "review",
                Modes = [new() { Name = "plan", Instructions = "A" }]
            }));

            var profile = CreateValidProfile("Research");
            profile.Instructions = " \r\n";
            await service.CreateAsync(profile);
            Assert.Null(profile.Instructions);

            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(CreateValidProfile("research")));
            var missingAvailableModes = CreateValidProfile("Missing available modes");
            missingAvailableModes.Instructions = "Current: {current_mode}";
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(missingAvailableModes));
            var missingCurrentMode = CreateValidProfile("Missing current mode");
            missingCurrentMode.Instructions = "Modes: {available_modes}";
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(missingCurrentMode));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static AgentModeProviderProfile CreateValidProfile(string name) => new()
    {
        Name = name,
        DefaultMode = "plan",
        Modes =
        [
            new AgentModeProfile { Name = "plan", Instructions = "Plan the work." },
            new AgentModeProfile { Name = "execute", Instructions = "Execute the plan." }
        ]
    };

    private static AgentModeProviderProfileService CreateService(string filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentModeProviderProfiles:FilePath"] = filePath })
            .Build();
        var logger = new LoggerFactory().CreateLogger<AgentModeProviderProfileRepository>();
        return new AgentModeProviderProfileService(new AgentModeProviderProfileRepository(configuration, logger));
    }
}
