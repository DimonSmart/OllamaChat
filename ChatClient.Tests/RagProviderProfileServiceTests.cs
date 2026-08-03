using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class RagProviderProfileServiceTests
{
    [Fact]
    public async Task CreateNormalizesFieldsAndAssignsTimestamps()
    {
        var repository = new InMemoryRepository();
        var service = new RagProviderProfileService(repository, new InMemoryAgentRepository());
        var profile = new RagProviderProfile { Id = Guid.Empty, Name = " Research ", FunctionToolDescription = "  Search docs  ", AdditionalContextInstructions = " ", CitationsPrompt = " " };

        await service.CreateAsync(profile);

        var stored = Assert.Single(await service.GetAllAsync());
        Assert.NotEqual(Guid.Empty, stored.Id);
        Assert.Equal("Research", stored.Name);
        Assert.Equal("Search docs", stored.FunctionToolDescription);
        Assert.Null(stored.AdditionalContextInstructions);
        Assert.Null(stored.CitationsPrompt);
        Assert.Equal(stored.CreatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task UpdatePreservesCreationTimestampAndRejectsInvalidValues()
    {
        var repository = new InMemoryRepository();
        var service = new RagProviderProfileService(repository, new InMemoryAgentRepository());
        var profile = new RagProviderProfile { Name = "Research" };
        await service.CreateAsync(profile);
        var created = profile.CreatedAt;
        profile.Name = "Updated";
        await service.UpdateAsync(profile);
        Assert.Equal(created, (await service.GetByIdAsync(profile.Id))!.CreatedAt);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new RagProviderProfile { Name = "updated" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new RagProviderProfile { Name = "bad", MaxResults = 51 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new RagProviderProfile { Name = "nan", MinRelevanceScore = double.NaN }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new RagProviderProfile { Name = "memory", RecentMessageMemoryLimit = -1 }));
    }

    [Fact]
    public async Task DeleteRejectsProfileUsedBySavedAgent()
    {
        var repository = new InMemoryRepository();
        var agents = new InMemoryAgentRepository();
        var service = new RagProviderProfileService(repository, agents);
        var profile = new RagProviderProfile { Name = "Research" };
        await service.CreateAsync(profile);
        agents.Items.Add(new AgentTemplateDefinition { AgentName = "Researcher", RagProviderProfileId = profile.Id });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(profile.Id));
        Assert.Contains("Research", error.Message);
        Assert.Contains("Researcher", error.Message);
    }

    private sealed class InMemoryRepository : IRagProviderProfileRepository
    {
        public List<RagProviderProfile> Items { get; private set; } = [];
        public Task<IReadOnlyCollection<RagProviderProfile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<RagProviderProfile>>(Items);
        public Task SaveAllAsync(List<RagProviderProfile> profiles, CancellationToken cancellationToken = default) { Items = profiles; return Task.CompletedTask; }
    }

    private sealed class InMemoryAgentRepository : IAgentTemplateRepository
    {
        public List<AgentTemplateDefinition> Items { get; } = [];
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<AgentTemplateDefinition>>(Items);
        public Task SaveAllAsync(List<AgentTemplateDefinition> agents, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
