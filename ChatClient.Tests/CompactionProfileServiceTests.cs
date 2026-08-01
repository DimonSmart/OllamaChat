using ChatClient.Api.Services;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class CompactionProfileServiceTests
{
    [Fact]
    public async Task CrudOperations_PersistProfilesNormalizeTextAndPreserveStageOrder()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var profile = CreatePipelineProfile("  Research  ");
            profile.Stages[2].SummaryInstructions = "\n Preserve facts. \n";

            await service.CreateAsync(profile);
            var createdAt = profile.CreatedAt;
            Assert.Equal("Research", profile.Name);
            Assert.Equal("Preserve facts.", profile.Stages[2].SummaryInstructions);
            Assert.Equal([CompactionStageKinds.ToolResult, CompactionStageKinds.Truncation, CompactionStageKinds.Summarization, CompactionStageKinds.SlidingWindow], profile.Stages.Select(stage => stage.Kind));

            profile.Name = "Research updated";
            await service.UpdateAsync(profile);
            var persisted = Assert.Single(await service.GetAllAsync());
            Assert.Equal(createdAt, persisted.CreatedAt);
            Assert.True(persisted.UpdatedAt >= createdAt);
            Assert.Equal(profile.Stages.Select(stage => stage.Kind), persisted.Stages.Select(stage => stage.Kind));

            await service.DeleteAsync(profile.Id);
            Assert.Empty(await service.GetAllAsync());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidProfilesAndDuplicateNames()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CompactionProfile { Name = " " }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CompactionProfile
            {
                Name = "Fixed",
                Kind = CompactionProfileKinds.ContextWindow,
                BudgetSource = CompactionBudgetSources.Fixed,
                ContextWindowTokens = 8_000,
                MaxOutputTokens = 8_000
            }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CompactionProfile
            {
                Name = "Pipeline",
                Kind = CompactionProfileKinds.CustomPipeline,
                Stages = [new CompactionStage { Kind = "unknown", TriggerTokenCount = 100, TargetTokenCount = 50 }]
            }));

            await service.CreateAsync(CreatePipelineProfile("Unique"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(CreatePipelineProfile("unique")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task DeleteAsync_RejectsProfileReferencedBySavedAgent_AndLegacyTemplateDeserializesWithoutProfile()
    {
        var root = CreateRoot();
        try
        {
            var profileRepository = CreateProfileRepository(root.FullName);
            var agentRepository = CreateAgentRepository(root.FullName);
            var seeder = new CompactionProfileSeeder(profileRepository);
            var service = new CompactionProfileService(profileRepository, agentRepository, seeder);
            var profile = CreatePipelineProfile("Used");
            await service.CreateAsync(profile);
            await agentRepository.SaveAllAsync([new AgentTemplateDefinition { AgentName = "Research agent", Content = "Research", CompactionProfileId = profile.Id }]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(profile.Id));
            Assert.Contains("Research agent", error.Message);

            await File.WriteAllTextAsync(Path.Combine(root.FullName, "UserData", "agent_templates.json"), "[{\"AgentName\":\"Legacy\",\"Content\":\"x\"}]");
            var legacy = Assert.Single(await agentRepository.GetAllAsync());
            Assert.Null(legacy.CompactionProfileId);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task RestoreBuiltInAsync_RestoresBalancedWithoutAssigningExistingAgents()
    {
        var root = CreateRoot();
        try
        {
            var profileRepository = CreateProfileRepository(root.FullName);
            var agentRepository = CreateAgentRepository(root.FullName);
            await agentRepository.SaveAllAsync([new AgentTemplateDefinition { AgentName = "Existing", Content = "x" }]);
            var service = new CompactionProfileService(profileRepository, agentRepository, new CompactionProfileSeeder(profileRepository));

            await service.RestoreBuiltInAsync();

            var balanced = Assert.Single(await service.GetAllAsync());
            Assert.Equal(CompactionProfileSeeder.BalancedProfileId, balanced.Id);
            Assert.Equal("Balanced", balanced.Name);
            Assert.Null((Assert.Single(await agentRepository.GetAllAsync())).CompactionProfileId);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CreateAsync_RejectsPartialSummarizerSelection_AndPreservesPrimaryAndSeparateModes()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var missingModel = CreatePipelineProfile("Missing model");
            missingModel.Stages[2].SummarizerLlmId = Guid.NewGuid();
            var missingModelError = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(missingModel));
            Assert.Contains("server ID and model name", missingModelError.Message);

            var missingServer = CreatePipelineProfile("Missing server");
            missingServer.Stages[2].SummarizerModelName = "  gpt-5-mini  ";
            var missingServerError = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(missingServer));
            Assert.Contains("server ID and model name", missingServerError.Message);

            var primary = CreatePipelineProfile("Primary");
            await service.CreateAsync(primary);
            Assert.Null(primary.Stages[2].SummarizerLlmId);
            Assert.Null(primary.Stages[2].SummarizerModelName);

            var separate = CreatePipelineProfile("Separate");
            var summarizerServerId = Guid.NewGuid();
            separate.Stages[2].SummarizerLlmId = summarizerServerId;
            separate.Stages[2].SummarizerModelName = "  gpt-5-mini  ";
            await service.CreateAsync(separate);

            var persisted = Assert.Single((await service.GetAllAsync()).Where(profile => profile.Id == separate.Id));
            Assert.Equal(summarizerServerId, persisted.Stages[2].SummarizerLlmId);
            Assert.Equal("gpt-5-mini", persisted.Stages[2].SummarizerModelName);
            Assert.Equal(separate.Stages.Select(stage => stage.Kind), persisted.Stages.Select(stage => stage.Kind));
        }
        finally { root.Delete(recursive: true); }
    }

    private static CompactionProfile CreatePipelineProfile(string name) => new()
    {
        Name = name,
        Kind = CompactionProfileKinds.CustomPipeline,
        BudgetSource = CompactionBudgetSources.SelectedModel,
        Stages =
        [
            new() { Kind = CompactionStageKinds.ToolResult, TriggerTokenCount = 8_000, TargetTokenCount = 4_000 },
            new() { Kind = CompactionStageKinds.Truncation, TriggerTokenCount = 6_000, TargetTokenCount = 3_000 },
            new() { Kind = CompactionStageKinds.Summarization, TriggerTokenCount = 4_000, TargetTokenCount = 2_000 },
            new() { Kind = CompactionStageKinds.SlidingWindow, TriggerTokenCount = 2_000, TargetTokenCount = 1_000 }
        ]
    };

    private static DirectoryInfo CreateRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "compaction-profile-tests", Guid.NewGuid().ToString("N")));

    private static CompactionProfileService CreateService(string root)
    {
        var repository = CreateProfileRepository(root);
        var agents = CreateAgentRepository(root);
        return new CompactionProfileService(repository, agents, new CompactionProfileSeeder(repository));
    }

    private static ICompactionProfileRepository CreateProfileRepository(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = root }).Build();
        return new CompactionProfileRepository(configuration, new LoggerFactory().CreateLogger<CompactionProfileRepository>());
    }

    private static IAgentTemplateRepository CreateAgentRepository(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = root }).Build();
        return new AgentTemplateRepository(configuration, new LoggerFactory().CreateLogger<AgentTemplateRepository>());
    }
}
