using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AgentSessionDefinitionResolverTests
{
    private static readonly AgentDefinitionReference Reference =
        new(AgentDefinitionKind.SavedAgent, Guid.NewGuid().ToString("D"));

    [Fact]
    public async Task ValidateAsync_RequiredModelMissing_ReturnsFailure()
    {
        var resolver = CreateResolver(new AgentDefinitionDescriptor
        {
            Reference = Reference,
            Name = "Agent",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.Required
        });

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest());

        Assert.False(validation.CanLaunch);
        Assert.Contains(validation.Problems, problem => problem.Message.Contains("model selection", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AgentModelRequirement.Optional)]
    [InlineData(AgentModelRequirement.None)]
    public async Task ValidateAsync_NonRequiredModelMissing_AllowsLaunch(AgentModelRequirement requirement)
    {
        var resolver = CreateResolver(new AgentDefinitionDescriptor
        {
            Reference = Reference,
            Name = "Agent",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = requirement
        });

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest());

        Assert.True(validation.CanLaunch);
    }

    [Fact]
    public async Task ValidateAsync_NumberUsesInvariantCulture()
    {
        var resolver = CreateResolver(DescriptorWithInput(AgentInputDefinitionKind.Number));

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest
        {
            Inputs = new Dictionary<string, string> { ["value"] = "1,23" }
        });

        Assert.True(validation.CanLaunch);
    }

    [Fact]
    public async Task ValidateAsync_InvalidJson_ReturnsFailure()
    {
        var resolver = CreateResolver(DescriptorWithInput(AgentInputDefinitionKind.Json));

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest
        {
            Inputs = new Dictionary<string, string> { ["value"] = "{ invalid" }
        });

        Assert.False(validation.CanLaunch);
        Assert.Contains(validation.Problems, problem => problem.Message == "Input 'Value' must contain valid JSON.");
    }

    [Fact]
    public async Task ResolveAsync_BooleanFalse_IsPreserved()
    {
        var resolver = CreateResolver(DescriptorWithInput(AgentInputDefinitionKind.Boolean));

        var resolved = await resolver.ResolveAsync(Reference, new AgentSessionDefinitionRequest
        {
            Inputs = new Dictionary<string, string> { ["value"] = bool.FalseString }
        });

        Assert.Equal(bool.FalseString, resolved.Inputs["value"]);
    }

    [Fact]
    public async Task ValidateAsync_PreflightProblem_ReturnsFailure()
    {
        var resolver = CreateResolver(
            new AgentDefinitionDescriptor
            {
                Reference = Reference,
                Name = "Workflow",
                RuntimeKind = AgentRuntimeKind.WorkflowAgent,
                ModelRequirement = AgentModelRequirement.None
            },
            [new AgentDefinitionLaunchProblem("preflight failed")]);

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest());

        Assert.False(validation.CanLaunch);
        Assert.Contains(validation.Problems, problem => problem.Message == "preflight failed");
    }

    [Fact]
    public async Task ValidateAsync_FileAccessWithModelWithoutFunctionCalling_ReturnsFailure()
    {
        var model = new ServerModel(Guid.NewGuid(), "no-functions");
        var resolver = CreateResolver(new AgentDefinitionDescriptor
        {
            Reference = Reference,
            Name = "Agent",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.Required,
            LaunchCapabilities = new AgentLaunchCapabilities { SupportsWorkspace = true }
        }, supportsFunctionCalling: false);

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest
        {
            UiModelSelection = new ServerModelSelection(model.ServerId, model.ModelName),
            Overrides = new AgentSessionOverrides { WorkspacePath = AppContext.BaseDirectory }
        });

        Assert.False(validation.CanLaunch);
        Assert.Contains(validation.Problems, problem => problem.Message.Contains("function calling required by File Access", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_NoFileAccessWithModelWithoutFunctionCalling_AllowsLaunch()
    {
        var model = new ServerModel(Guid.NewGuid(), "no-functions");
        var resolver = CreateResolver(new AgentDefinitionDescriptor
        {
            Reference = Reference,
            Name = "Agent",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.Required
        }, supportsFunctionCalling: false);

        var validation = await resolver.ValidateAsync(Reference, new AgentSessionDefinitionRequest
        {
            UiModelSelection = new ServerModelSelection(model.ServerId, model.ModelName)
        });

        Assert.True(validation.CanLaunch);
        Assert.DoesNotContain(validation.Problems, problem => problem.Message.Contains("File Access", StringComparison.OrdinalIgnoreCase));
    }

    private static AgentDefinitionDescriptor DescriptorWithInput(AgentInputDefinitionKind kind) =>
        new()
        {
            Reference = Reference,
            Name = "Agent",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.None,
            Inputs =
            [
                new AgentInputDefinition
                {
                    Key = "value",
                    DisplayName = "Value",
                    Kind = kind,
                    IsRequired = true
                }
            ]
        };

    private static AgentSessionDefinitionResolver CreateResolver(
        AgentDefinitionDescriptor descriptor,
        IReadOnlyList<AgentDefinitionLaunchProblem>? preflightProblems = null,
        bool supportsFunctionCalling = true) =>
        new(
            new StubCatalog(descriptor),
            new StubPreflightValidator(preflightProblems ?? []),
            new StubLaunchCapabilityValidator(supportsFunctionCalling),
            new StubSandboxProfileService(),
            new StubSandboxProviderRegistry());

    private sealed class StubCatalog(AgentDefinitionDescriptor descriptor) : IAgentDefinitionCatalog
    {
        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>([descriptor]);

        public Task<AgentDefinitionDescriptor?> FindAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentDefinitionDescriptor?>(descriptor);

        public Task<AgentDefinitionDescriptor> GetRequiredAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(descriptor);
    }

    private sealed class StubPreflightValidator(IReadOnlyList<AgentDefinitionLaunchProblem> problems)
        : IWorkflowDefinitionPreflightValidator
    {
        public Task<IReadOnlyList<AgentDefinitionLaunchProblem>> ValidateAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(problems);
    }

    private sealed class StubLaunchCapabilityValidator(bool supportsFunctionCalling)
        : IAgentLaunchCapabilityValidator
    {
        public Task<bool> SupportsFunctionCallingAsync(
            ServerModel model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(supportsFunctionCalling);
    }

    private sealed class StubSandboxProfileService : ISandboxProfileService
    {
        public Task<IReadOnlyCollection<SandboxProfile>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<SandboxProfile>>([]);

        public Task<SandboxProfile?> GetByIdAsync(Guid id) =>
            Task.FromResult<SandboxProfile?>(null);

        public Task CreateAsync(SandboxProfile profile) => Task.CompletedTask;

        public Task UpdateAsync(SandboxProfile profile) => Task.CompletedTask;

        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    private sealed class StubSandboxProviderRegistry : ISandboxProviderRegistry
    {
        public IReadOnlyList<SandboxProviderDescriptor> GetProviders() => [];

        public ISandboxProvider GetRequired(string providerType) =>
            throw new KeyNotFoundException(providerType);

        public bool TryGet(string providerType, out ISandboxProvider provider)
        {
            provider = null!;
            return false;
        }
    }
}
