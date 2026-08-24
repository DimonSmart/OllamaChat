using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AgentDefinitionLaunchCapabilityAnalyzerTests
{
    [Fact]
    public async Task Catalog_SavedAgentInheritsWorkspaceAndSandboxFromBackgroundAgent()
    {
        var worker = Agent("Worker", enableShell: true);
        var coordinator = Agent("Coordinator", backgroundAgentIds: [worker.Id]);
        var agentService = new StubAgentTemplateService([coordinator, worker]);
        var analyzer = CreateAnalyzer(agentService);
        var catalog = new AgentDefinitionCatalog(
            agentService,
            new EmptyWorkflowDefinitionService(),
            new EmptyInputDefinitionProvider(),
            new RequiredModelAnalyzer(),
            analyzer);

        var descriptor = await catalog.FindAsync(
            new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                coordinator.Id.ToString("D")),
            TestContext.Current.CancellationToken);

        Assert.NotNull(descriptor);
        Assert.True(descriptor.LaunchCapabilities.SupportsWorkspace);
        Assert.True(descriptor.LaunchCapabilities.SupportsSandboxProfile);
        Assert.True(descriptor.LaunchCapabilities.SupportsMcpBindingOverrides);
        Assert.Empty(descriptor.DefinitionProblems);
    }

    [Fact]
    public async Task AnalyzeAsync_BackgroundAgentCycle_TerminatesAndRetainsCapabilities()
    {
        var first = Agent("First");
        var second = Agent("Second", enableShell: true);
        first.BackgroundAgentIds = [second.Id];
        second.BackgroundAgentIds = [first.Id];
        var agentService = new StubAgentTemplateService([first, second]);
        var analyzer = CreateAnalyzer(agentService);

        var capabilities = await analyzer.AnalyzeAsync(
            new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                first.Id.ToString("D")),
            TestContext.Current.CancellationToken);

        Assert.True(capabilities.SupportsWorkspace);
        Assert.True(capabilities.SupportsSandboxProfile);
        Assert.Equal(1, agentService.GetCounts[first.Id]);
        Assert.Equal(1, agentService.GetCounts[second.Id]);
    }

    private static AgentDefinitionLaunchCapabilityAnalyzer CreateAnalyzer(
        IAgentTemplateService agentService) =>
        new(
            agentService,
            new EmptyWorkflowDefinitionService(),
            new UnsupportedWorkflowDefinitionCompiler(),
            new LegacyWorkflowDefinitionNormalizer(agentService));

    private static AgentTemplateDefinition Agent(
        string name,
        bool enableShell = false,
        IReadOnlyList<Guid>? backgroundAgentIds = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            AgentName = name,
            EnableShell = enableShell,
            BackgroundAgentIds = backgroundAgentIds?.ToList() ?? []
        };

    private sealed class StubAgentTemplateService(
        IReadOnlyCollection<AgentTemplateDefinition> agents) : IAgentTemplateService
    {
        public Dictionary<Guid, int> GetCounts { get; } = [];

        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() =>
            Task.FromResult(agents);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid templateId)
        {
            GetCounts[templateId] = GetCounts.GetValueOrDefault(templateId) + 1;
            return Task.FromResult(agents.FirstOrDefault(agent => agent.Id == templateId));
        }

        public Task CreateAsync(AgentTemplateDefinition template) => throw new NotSupportedException();

        public Task UpdateAsync(AgentTemplateDefinition template) => throw new NotSupportedException();

        public Task DeleteAsync(Guid templateId) => throw new NotSupportedException();
    }

    private sealed class EmptyWorkflowDefinitionService : IWorkflowDefinitionService
    {
        public Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<SavedWorkflowDefinition>>([]);

        public Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId) =>
            Task.FromResult<SavedWorkflowDefinition?>(null);

        public Task CreateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task UpdateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task DeleteAsync(Guid workflowId) => throw new NotSupportedException();
    }

    private sealed class UnsupportedWorkflowDefinitionCompiler : IWorkflowDefinitionCompiler
    {
        public Task<CompiledWorkflowDefinition> CompileAsync(
            string sourceCode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyInputDefinitionProvider : IAgentInputDefinitionProvider
    {
        public Task<IReadOnlyList<AgentInputDefinition>> GetInputsAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentInputDefinition>>([]);
    }

    private sealed class RequiredModelAnalyzer : IAgentDefinitionModelRequirementAnalyzer
    {
        public Task<AgentModelRequirement> AnalyzeAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AgentModelRequirement.Required);
    }
}
