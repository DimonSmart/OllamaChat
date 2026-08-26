using ChatClient.Api.Services;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public sealed class ModelRuntimeLimitsServiceTests
{
    [Fact]
    public async Task CreateAndUpdate_PersistLimitsWithCaseInsensitiveUniqueness()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var limits = CreateLimits(" GPT-5 ");
            await service.CreateAsync(limits);
            var createdAt = limits.CreatedAt;

            Assert.Equal("GPT-5", limits.ModelName);
            Assert.NotNull(await service.GetAsync(limits.ServerId, "gpt-5"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(CreateLimits("gpt-5", limits.ServerId)));

            limits.ModelName = "gPt-5";
            limits.MaxOutputTokens = 16_000;
            await service.UpdateAsync(limits);
            var saved = Assert.Single(await service.GetAllAsync());
            Assert.Equal(createdAt, saved.CreatedAt);
            Assert.Equal("gPt-5", saved.ModelName);
            Assert.Equal(16_000, saved.MaxOutputTokens);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(100, 101)]
    public async Task CreateAsync_RejectsInvalidLimits(int contextWindowTokens, int maxOutputTokens)
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var limits = CreateLimits("test");
            limits.ContextWindowTokens = contextWindowTokens;
            limits.MaxOutputTokens = maxOutputTokens;
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(limits));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CreateAsync_AllowsContextOnlyAndFullWindowOutputMetadata()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var serverId = Guid.NewGuid();
            var contextOnly = CreateLimits("context-only", serverId);
            contextOnly.MaxOutputTokens = null;
            await service.CreateAsync(contextOnly);

            var fullWindowOutput = CreateLimits("full-window-output", serverId);
            fullWindowOutput.ContextWindowTokens = 131_072;
            fullWindowOutput.MaxOutputTokens = 131_072;
            await service.CreateAsync(fullWindowOutput);

            Assert.Null((await service.GetAsync(serverId, "context-only"))!.MaxOutputTokens);
            Assert.Equal(131_072, (await service.GetAsync(serverId, "full-window-output"))!.MaxOutputTokens);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task PersistedLimits_RemainAvailableWhenModelIsNotDiscovered()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var limits = CreateLimits("unavailable-model");
            await service.CreateAsync(limits);

            var reloaded = CreateService(root.FullName);
            var persisted = await reloaded.GetAsync(limits.ServerId, "UNAVAILABLE-MODEL");
            Assert.NotNull(persisted);
            Assert.Equal(128_000, persisted!.ContextWindowTokens);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task FillKnownAsync_AddsCatalogAndFallbackModels_WithoutReplacingExistingLimits()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var azureServerId = Guid.NewGuid();
            var ollamaServerId = Guid.NewGuid();
            var existing = CreateLimits("gpt-4o", azureServerId);
            existing.ContextWindowTokens = 64_000;
            existing.MaxOutputTokens = 8_000;
            await service.CreateAsync(existing);

            var result = await service.FillKnownAsync([
                new ServerModel(azureServerId, "gpt-5.6-sol"),
                new ServerModel(azureServerId, "GPT-4O"),
                new ServerModel(ollamaServerId, "qwen3:latest"),
                new ServerModel(ollamaServerId, "custom-model")
            ], defaultContextWindowTokens: 96_000);

            Assert.Equal(new ModelRuntimeLimitsFillResult(3, 1, 1), result);

            var gpt56 = (await service.GetAsync(azureServerId, "gpt-5.6-sol"))!;
            Assert.Equal(1_050_000, gpt56.ContextWindowTokens);
            Assert.Equal(128_000, gpt56.MaxOutputTokens);

            var qwen3 = (await service.GetAsync(ollamaServerId, "qwen3:latest"))!;
            Assert.Equal(40_960, qwen3.ContextWindowTokens);
            Assert.Null(qwen3.MaxOutputTokens);

            Assert.Equal(64_000, (await service.GetAsync(azureServerId, "gpt-4o"))!.ContextWindowTokens);

            var fallback = (await service.GetAsync(ollamaServerId, "custom-model"))!;
            Assert.Equal(96_000, fallback.ContextWindowTokens);
            Assert.Null(fallback.MaxOutputTokens);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Resolver_ResolvesSelectedAndFixedBudgets_AndReportsMissingOrIncompleteLimits()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root.FullName);
            var limits = CreateLimits("gpt-5");
            await service.CreateAsync(limits);
            ICompactionBudgetResolver resolver = new CompactionBudgetResolver(service);
            var model = new ServerModel(limits.ServerId, "GPT-5");
            var selected = await resolver.ResolveAsync(new CompactionProfile { Name = "Selected", BudgetSource = CompactionBudgetSources.SelectedModel }, model);
            Assert.Equal(new CompactionBudget(128_000, 8_000, 120_000), selected);

            var fixedBudget = await resolver.ResolveAsync(new CompactionProfile
            {
                Name = "Fixed",
                BudgetSource = CompactionBudgetSources.Fixed,
                ContextWindowTokens = 64_000,
                MaxOutputTokens = 4_000
            }, model);
            Assert.Equal(new CompactionBudget(64_000, 4_000, 60_000), fixedBudget);

            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
                new CompactionProfile { Name = "Missing", BudgetSource = CompactionBudgetSources.SelectedModel },
                new ServerModel(Guid.NewGuid(), "not-installed")));
            Assert.Contains("Missing", missing.Message);
            Assert.Contains("not-installed", missing.Message);

            var incomplete = CreateLimits("context-only");
            incomplete.MaxOutputTokens = null;
            await service.CreateAsync(incomplete);
            var incompleteError = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
                new CompactionProfile { Name = "Incomplete", BudgetSource = CompactionBudgetSources.SelectedModel },
                new ServerModel(incomplete.ServerId, incomplete.ModelName)));
            Assert.Contains("maximum output", incompleteError.Message.ToLowerInvariant());
        }
        finally { root.Delete(recursive: true); }
    }

    private static ModelRuntimeLimits CreateLimits(string modelName, Guid? serverId = null) => new()
    {
        ServerId = serverId ?? Guid.NewGuid(),
        ModelName = modelName,
        ContextWindowTokens = 128_000,
        MaxOutputTokens = 8_000
    };

    private static DirectoryInfo CreateRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "model-runtime-limits-tests", Guid.NewGuid().ToString("N")));

    private static ModelRuntimeLimitsService CreateService(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:RootPath"] = root }).Build();
        IModelRuntimeLimitsRepository repository = new ModelRuntimeLimitsRepository(configuration, new LoggerFactory().CreateLogger<ModelRuntimeLimitsRepository>());
        return new ModelRuntimeLimitsService(repository);
    }
}
