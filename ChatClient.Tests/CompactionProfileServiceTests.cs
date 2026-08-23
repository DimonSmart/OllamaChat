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
            profile.Stages[0].Kind = CompactionStageKinds.Truncation;
            profile.Stages.Reverse();
            var expectedStageIds = profile.Stages.Select(stage => stage.Id).ToArray();
            await service.UpdateAsync(profile);
            var persisted = Assert.Single(await service.GetAllAsync());
            Assert.Equal(createdAt, persisted.CreatedAt);
            Assert.True(persisted.UpdatedAt >= createdAt);
            Assert.Equal(profile.Stages.Select(stage => stage.Kind), persisted.Stages.Select(stage => stage.Kind));
            Assert.Equal(expectedStageIds, persisted.Stages.Select(stage => stage.Id));

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
                Name = "Invalid thresholds",
                ToolResultThreshold = .81,
                TruncationThreshold = .80
            }));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CompactionProfile
            {
                Name = "Pipeline",
                Kind = CompactionProfileKinds.CustomPipeline,
                Stages = [new CompactionStage { Kind = "unknown", Trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 100 }, Target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 50 } }]
            }));

            await service.CreateAsync(CreatePipelineProfile("Unique"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(CreatePipelineProfile("unique")));

            var zeroTarget = CreatePipelineProfile("Zero target");
            zeroTarget.Stages[0].Target.Value = 0;
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(zeroTarget));

            var tokenTargetZero = CreatePipelineProfile("Token target zero");
            tokenTargetZero.Stages[0].Trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 100 };
            tokenTargetZero.Stages[0].Target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 0 };
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(tokenTargetZero));

            var tokenTriggerZero = CreatePipelineProfile("Token trigger zero");
            tokenTriggerZero.Stages[0].Trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 0 };
            tokenTriggerZero.Stages[0].Target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = -1 };
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(tokenTriggerZero));
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
            await agentRepository.SaveAllAsync([new AgentTemplateDefinition { AgentName = "Research agent", Content = "Research", CompactionProfileId = profile.Id }], cancellationToken: TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(profile.Id));
            Assert.Contains("Research agent", error.Message);

            await File.WriteAllTextAsync(Path.Combine(root.FullName, "UserData", "agent_templates.json"), "[{\"AgentName\":\"Legacy\",\"Content\":\"x\"}]", cancellationToken: TestContext.Current.CancellationToken);
            var legacy = Assert.Single(await agentRepository.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken));
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
            await agentRepository.SaveAllAsync([new AgentTemplateDefinition { AgentName = "Existing", Content = "x" }], cancellationToken: TestContext.Current.CancellationToken);
            var service = new CompactionProfileService(profileRepository, agentRepository, new CompactionProfileSeeder(profileRepository));

            await service.RestoreBuiltInAsync();

            var balanced = Assert.Single(await service.GetAllAsync());
            Assert.Equal(CompactionProfileSeeder.BalancedProfileId, balanced.Id);
            Assert.Equal("Balanced", balanced.Name);
            Assert.Null((Assert.Single(await agentRepository.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken))).CompactionProfileId);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task SeedAsync_UpdatesBalancedByStableIdWithoutOverwritingUserProfiles()
    {
        var root = CreateRoot();
        try
        {
            var repository = CreateProfileRepository(root.FullName);
            var seeder = new CompactionProfileSeeder(repository);
            var userProfile = new CompactionProfile { Name = "My balanced profile" };
            var outdatedBalanced = CompactionProfileSeeder.CreateBalancedProfile();
            outdatedBalanced.BudgetSource = CompactionBudgetSources.Fixed;
            outdatedBalanced.ContextWindowTokens = 128_000;
            outdatedBalanced.MaxOutputTokens = 8_000;
            await repository.SaveAllAsync([userProfile, outdatedBalanced], cancellationToken: TestContext.Current.CancellationToken);

            await seeder.SeedAsync();

            var profiles = await repository.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
            var balanced = Assert.Single(profiles, profile => profile.Id == CompactionProfileSeeder.BalancedProfileId);
            Assert.Equal(CompactionBudgetSources.SelectedModel, balanced.BudgetSource);
            Assert.Null(balanced.ContextWindowTokens);
            Assert.Null(balanced.MaxOutputTokens);
            Assert.Equal(.50, balanced.ToolResultThreshold);
            Assert.Equal(.80, balanced.TruncationThreshold);
            Assert.Equal(userProfile.Id, Assert.Single(profiles, profile => profile.Id == userProfile.Id).Id);
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

            var persisted = Assert.Single((await service.GetAllAsync()), profile => profile.Id == separate.Id);
            Assert.Equal(summarizerServerId, persisted.Stages[2].SummarizerLlmId);
            Assert.Equal("gpt-5-mini", persisted.Stages[2].SummarizerModelName);
            Assert.Equal(separate.Stages.Select(stage => stage.Kind), persisted.Stages.Select(stage => stage.Kind));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CreateAsync_NormalizesFieldsThatDoNotApplyToTheSelectedStageKind()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var profile = CreatePipelineProfile("Normalize stage fields");
            var nonSummarizationStages = new[] { profile.Stages[0], profile.Stages[1], profile.Stages[3] };
            foreach (var stage in nonSummarizationStages)
            {
                stage.SummaryInstructions = "Ignore this";
                stage.SummarizerLlmId = Guid.NewGuid();
                stage.SummarizerModelName = "summary-model";
            }

            var toolResult = profile.Stages[0];
            toolResult.MinimumPreservedTurns = 5;
            var slidingWindow = profile.Stages[3];
            slidingWindow.MinimumPreservedGroups = 5;

            await service.CreateAsync(profile);

            Assert.Equal(0, toolResult.MinimumPreservedTurns);
            Assert.All(nonSummarizationStages, stage =>
            {
                Assert.Null(stage.SummaryInstructions);
                Assert.Null(stage.SummarizerLlmId);
                Assert.Null(stage.SummarizerModelName);
            });
            Assert.Equal(0, slidingWindow.MinimumPreservedGroups);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task GetAllAsync_MigratesLegacyTokenStagesAndResavesModernLimits()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root.FullName, "UserData", "compaction_profiles.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "[{\"Name\":\"Legacy\",\"Kind\":\"custom-pipeline\",\"Stages\":[{\"Kind\":\"tool-result\",\"TriggerTokenCount\":8000,\"TargetTokenCount\":4000}]}]", cancellationToken: TestContext.Current.CancellationToken);

            var profile = Assert.Single(await CreateProfileRepository(root.FullName).GetAllAsync(cancellationToken: TestContext.Current.CancellationToken));
            var stage = Assert.Single(profile.Stages);
            Assert.Equal(CompactionLimitKinds.Tokens, stage.Trigger.Kind);
            Assert.Equal(8_000, stage.Trigger.Value);
            Assert.Equal(4_000, stage.Target.Value);

            var resaved = await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("TriggerTokenCount", resaved);
            Assert.Contains("\"Trigger\"", resaved);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void CompactionStageDefaults_UseRecommendedLimitsAndRetainedItems()
    {
        var toolResult = CompactionStageDefaults.Create(CompactionStageKinds.ToolResult);
        var summarization = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        var truncation = CompactionStageDefaults.Create(CompactionStageKinds.Truncation);
        var slidingWindow = CompactionStageDefaults.Create(CompactionStageKinds.SlidingWindow);

        Assert.Equal((CompactionLimitKinds.InputBudgetPercent, .45d, .35d, 8, 0), (toolResult.Trigger.Kind, toolResult.Trigger.Value, toolResult.Target.Value, toolResult.MinimumPreservedGroups, toolResult.MinimumPreservedTurns));
        Assert.Equal((CompactionLimitKinds.InputBudgetPercent, .65d, .50d, 8, 0), (summarization.Trigger.Kind, summarization.Trigger.Value, summarization.Target.Value, summarization.MinimumPreservedGroups, summarization.MinimumPreservedTurns));
        Assert.Equal((CompactionLimitKinds.InputBudgetPercent, .80d, .70d, 4, 0), (truncation.Trigger.Kind, truncation.Trigger.Value, truncation.Target.Value, truncation.MinimumPreservedGroups, truncation.MinimumPreservedTurns));
        Assert.Equal((CompactionLimitKinds.Turns, 20d, 12d, 0, 8), (slidingWindow.Trigger.Kind, slidingWindow.Trigger.Value, slidingWindow.Target.Value, slidingWindow.MinimumPreservedGroups, slidingWindow.MinimumPreservedTurns));
        Assert.NotEqual(Guid.Empty, toolResult.Id);
    }

    [Fact]
    public async Task GetAllAsync_NormalizesMissingAndDuplicateStageIds()
    {
        var root = CreateRoot();
        try
        {
            var stageId = Guid.NewGuid();
            var path = Path.Combine(root.FullName, "UserData", "compaction_profiles.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var legacy = new CompactionProfile
            {
                Name = "Identity",
                Kind = CompactionProfileKinds.CustomPipeline,
                Stages =
                [
                    CompactionStageDefaults.Create(CompactionStageKinds.ToolResult),
                    CompactionStageDefaults.Create(CompactionStageKinds.Truncation),
                    CompactionStageDefaults.Create(CompactionStageKinds.Summarization)
                ]
            };
            legacy.Stages[0].Id = stageId;
            legacy.Stages[1].Id = stageId;
            legacy.Stages[2].Id = Guid.Empty;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { legacy }), cancellationToken: TestContext.Current.CancellationToken);

            var profile = Assert.Single(await CreateProfileRepository(root.FullName).GetAllAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(3, profile.Stages.Select(stage => stage.Id).Distinct().Count());
            Assert.All(profile.Stages, stage => Assert.NotEqual(Guid.Empty, stage.Id));

            var resaved = await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(resaved, "\\\"Id\\\"").Count);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task GetAllAsync_MigratesLegacyPercentLimitsAndRetainedCount()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root.FullName, "UserData", "compaction_profiles.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, """
                [{"Name":"Legacy","Kind":"custom-pipeline","Stages":[
                  {"Kind":"tool-result","Trigger":{"Kind":"input-budget-percentage","Value":45},"Target":{"Kind":"input-budget-percentage","Value":35},"RetainedCount":8},
                  {"Kind":"sliding-window","Trigger":{"Kind":"turns","Value":20},"Target":{"Kind":"turns","Value":12},"RetainedCount":6}
                ]}]
                """, cancellationToken: TestContext.Current.CancellationToken);

            var stages = Assert.Single(await CreateProfileRepository(root.FullName).GetAllAsync(cancellationToken: TestContext.Current.CancellationToken)).Stages;

            Assert.Equal((CompactionLimitKinds.InputBudgetPercent, .45d, .35d, 8, 0), (stages[0].Trigger.Kind, stages[0].Trigger.Value, stages[0].Target.Value, stages[0].MinimumPreservedGroups, stages[0].MinimumPreservedTurns));
            Assert.Equal((CompactionLimitKinds.Turns, 20d, 12d, 0, 6), (stages[1].Trigger.Kind, stages[1].Trigger.Value, stages[1].Target.Value, stages[1].MinimumPreservedGroups, stages[1].MinimumPreservedTurns));
            var resaved = await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("RetainedCount", resaved);
            Assert.DoesNotContain("input-budget-percentage", resaved);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task GetAllAsync_MigratesLegacyContextWindowPercentagesToFractionalThresholds()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root.FullName, "UserData", "compaction_profiles.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "[{\"Name\":\"Legacy\",\"ToolResultThresholdPercentage\":50,\"TruncationThresholdPercentage\":80}]", cancellationToken: TestContext.Current.CancellationToken);

            var profile = Assert.Single(await CreateProfileRepository(root.FullName).GetAllAsync(cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(.50, profile.ToolResultThreshold);
            Assert.Equal(.80, profile.TruncationThreshold);
            var resaved = await File.ReadAllTextAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("ThresholdPercentage", resaved);
            Assert.Contains("ToolResultThreshold", resaved);
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
            CompactionStageDefaults.Create(CompactionStageKinds.ToolResult),
            CompactionStageDefaults.Create(CompactionStageKinds.Truncation),
            CompactionStageDefaults.Create(CompactionStageKinds.Summarization),
            CompactionStageDefaults.Create(CompactionStageKinds.SlidingWindow)
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
