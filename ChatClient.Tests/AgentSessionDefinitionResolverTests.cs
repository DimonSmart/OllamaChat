using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
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
        IReadOnlyList<AgentDefinitionLaunchProblem>? preflightProblems = null) =>
        new(new StubCatalog(descriptor), new StubPreflightValidator(preflightProblems ?? []));

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
}
