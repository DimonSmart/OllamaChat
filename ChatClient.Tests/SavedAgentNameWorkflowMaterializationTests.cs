using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class SavedAgentNameWorkflowMaterializationTests
{
    [Fact]
    [Obsolete]
    public async Task MaterializeAsync_ResolvesLegacyAndExpressiveSavedAgentNames()
    {
        var kant = CreateAgent("Immanuel Kant", "Kant prompt");
        var nietzsche = CreateAgent("Friedrich Nietzsche", "Nietzsche prompt");
        var agentService = new StubAgentTemplateService([kant, nietzsche]);
        var resolver = new NormalizingWorkflowParticipantResolver(
            new LegacyWorkflowDefinitionNormalizer(agentService),
            new WorkflowParticipantResolver(
                agentService,
                new StubDefinitionCatalog([kant, nietzsche])));
        var materializer = new WorkflowAgentDraftMaterializer(resolver);

        const string sourceCode =
            """
            var workflow = WorkflowDefinitionBuilder
                .New("saved-agent-name-demo", "Saved Agent Name Demo")
                .Agent("debater_a", agent => agent
                    .FromSavedAgent("Immanuel Kant")
                    .Role("Kantian philosopher"))
                .Agent("debater_b", agent => agent
                    .UseAgentByName("Friedrich Nietzsche")
                    .Role("Nietzschean philosopher"))
                .UseGroupChat(groupChat => groupChat
                    .Participants("debater_a", "debater_b")
                    .UseRoundRobinManager())
                .Build();

            workflow
            """;

        var compiled = await new WorkflowDefinitionCompiler().CompileAsync(sourceCode);
        var materialized = await materializer.MaterializeAsync(compiled.Workflow!);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(materialized);

        AssertMaterializedAgent(workflow, "debater_a", kant);
        AssertMaterializedAgent(workflow, "debater_b", nietzsche);
    }

    private static void AssertMaterializedAgent(
        GroupChatWorkflowDefinition workflow,
        string participantId,
        AgentTemplateDefinition expectedAgent)
    {
        var participant = Assert.Single(
            workflow.Participants,
            candidate => string.Equals(candidate.Id, participantId, StringComparison.Ordinal));
        var source = Assert.IsType<InlineAgentParticipantSource>(participant.Source);

        Assert.Equal(expectedAgent.Id, source.Agent.Id);
        Assert.Equal(expectedAgent.AgentName, source.Agent.AgentName);
        Assert.Equal(participantId, source.Agent.RuntimeAgentId);
    }

    private static AgentTemplateDefinition CreateAgent(string name, string prompt) =>
        new()
        {
            Id = Guid.NewGuid(),
            AgentName = name,
            ShortName = name.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant(),
            Content = prompt
        };

    private sealed class StubAgentTemplateService(
        IReadOnlyCollection<AgentTemplateDefinition> agents) : IAgentTemplateService
    {
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() =>
            Task.FromResult(agents);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid templateId) =>
            Task.FromResult(agents.FirstOrDefault(agent => agent.Id == templateId));

        public Task CreateAsync(AgentTemplateDefinition template) => throw new NotSupportedException();

        public Task UpdateAsync(AgentTemplateDefinition template) => throw new NotSupportedException();

        public Task DeleteAsync(Guid templateId) => throw new NotSupportedException();
    }

    private sealed class StubDefinitionCatalog(
        IReadOnlyCollection<AgentTemplateDefinition> agents) : IAgentDefinitionCatalog
    {
        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>(
                agents.Select(static agent => new AgentDefinitionDescriptor
                {
                    Reference = new AgentDefinitionReference(
                        AgentDefinitionKind.SavedAgent,
                        agent.Id.ToString("D")),
                    Name = agent.AgentName,
                    Description = agent.Summary,
                    RuntimeKind = AgentRuntimeKind.LlmAgent,
                    ModelRequirement = AgentModelRequirement.Required
                }).ToList());

        public async Task<AgentDefinitionDescriptor?> FindAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            (await GetAllAsync(cancellationToken)).FirstOrDefault(item =>
                AgentDefinitionReferenceComparer.Instance.Equals(item.Reference, reference));

        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            await FindAsync(reference, cancellationToken) ??
            throw new KeyNotFoundException(
                $"Saved definition '{reference.Kind}:{reference.Id}' was not found.");
    }
}
