using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class RecursiveWorkflowExecutionIntegrationTests
{
    [Fact]
    public async Task RunAsync_SequentialWorkflows_ExecutesNestedDefinitionsThroughProductionRunner()
    {
        var system = CreateSystem(8,
            Workflow("root", "Root", AgentParticipant("a", "A"), WorkflowParticipant("nested", "Nested"), AgentParticipant("d", "D")),
            Workflow("nested", "B", AgentParticipant("b", "B"), AgentParticipant("c", "C")));

        var events = await system.RunAsync("root");

        AssertValidAgentRunProtocol(events);
        Assert.Equal(["a", "b", "c", "d"], system.LlmFactory.Invocations.Select(static item => item.Id));
        Assert.Equal(2, system.WorkflowFactory.CreatedWorkflowIds.Count);
        Assert.Equal(4, system.LlmFactory.Invocations.Select(static item => item.Context.RunId).Distinct().Count());
        Assert.All(system.LlmFactory.Invocations, static item => Assert.NotNull(item.Context.ParentRunId));
        Assert.All(system.LlmFactory.Invocations.Where(static item => item.Id is "b" or "c"), static item =>
            Assert.Equal(["root", "nested", item.Id], item.Context.DefinitionStack.Select(static frame => frame.Definition.Id)));
        Assert.Equal("d", Assert.IsType<AgentRunCompleted>(events[^1]).Result.FinalMessage.Content);
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "b", "c", "a")]
    public async Task RunAsync_CyclicWorkflows_FailsBeforeLeafRuntimeExecutes(params string[] path)
    {
        var workflows = path.Take(path.Length - 1)
            .Select((id, index) => Workflow(id, id, WorkflowParticipant(path[index + 1], path[index + 1])))
            .ToArray();
        var system = CreateSystem(8, workflows);

        var events = await system.RunAsync("a");

        AssertValidAgentRunProtocol(events);
        var failure = Assert.IsType<AgentRunFailed>(events[^1]);
        Assert.Equal("workflow_cycle_detected", failure.Error.Code);
        Assert.False(failure.Error.IsRetryable);
        Assert.Empty(system.LlmFactory.Invocations);
        Assert.Equal(path, failure.Error.Metadata["definition.stack"].Split(" > ").Select(static frame => frame.Split(':')[1]));
    }

    [Fact]
    public async Task RunAsync_WorkflowNestingLimit_FailsBeforeFourthWorkflowRuntimeStarts()
    {
        var system = CreateSystem(3,
            Workflow("a", "A", WorkflowParticipant("b", "B")),
            Workflow("b", "B", WorkflowParticipant("c", "C")),
            Workflow("c", "C", WorkflowParticipant("d", "D")),
            Workflow("d", "D", AgentParticipant("leaf", "Leaf")));

        var events = await system.RunAsync("a");

        AssertValidAgentRunProtocol(events);
        var failure = Assert.IsType<AgentRunFailed>(events[^1]);
        Assert.Equal("workflow_nesting_limit_exceeded", failure.Error.Code);
        Assert.Contains("3", failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal(["a", "b", "c"], system.WorkflowFactory.CreatedWorkflowIds);
        Assert.Empty(system.LlmFactory.Invocations);
    }

    private static TestSystem CreateSystem(int maximumDepth, params WorkflowSpec[] workflows)
    {
        var catalog = new Catalog(workflows);
        var contexts = new AgentRunContextFactory();
        var protocol = new AgentRuntimeProtocolExecutor(NullLogger<AgentRuntimeProtocolExecutor>.Instance);
        var llmFactory = new FakeLlmFactory();
        IAgentRunner? runner = null;
        var invoker = new WorkflowParticipantInvoker(
            contexts, () => runner!, new UnsupportedInlineFactory(),
            new AgentRunNestingValidator(new AgentRuntimeOptions { MaximumWorkflowNestingDepth = maximumDepth }), protocol);
        var workflowFactory = new FakeWorkflowFactory(workflows, invoker);
        runner = new AgentRunner(catalog,
            new AgentRunNestingValidator(new AgentRuntimeOptions { MaximumWorkflowNestingDepth = maximumDepth }),
            new AgentRuntimeFactory(llmFactory, workflowFactory), protocol, NullLogger<AgentRunner>.Instance);
        return new TestSystem(runner, catalog, contexts, llmFactory, workflowFactory);
    }

    private static WorkflowSpec Workflow(string id, string name, params ResolvedWorkflowParticipant[] participants) => new(id, name, participants);
    private static ResolvedWorkflowParticipant AgentParticipant(string id, string name) => Participant(id, name, AgentDefinitionKind.SavedAgent, AgentRuntimeKind.LlmAgent);
    private static ResolvedWorkflowParticipant WorkflowParticipant(string id, string name) => Participant(id, name, AgentDefinitionKind.SavedWorkflow, AgentRuntimeKind.WorkflowAgent);
    private static ResolvedWorkflowParticipant Participant(string id, string name, AgentDefinitionKind kind, AgentRuntimeKind runtimeKind) => new()
    {
        ParticipantId = id,
        DisplayName = name,
        RuntimeKind = runtimeKind,
        Source = new ReferencedParticipantSource(new AgentDefinitionReference(kind, id))
    };

    private static void AssertValidAgentRunProtocol(IReadOnlyList<AgentRunEvent> events)
    {
        var terminals = events.Where(static item => item is AgentRunCompleted or AgentRunFailed).ToList();
        Assert.Single(terminals);
        Assert.Same(terminals[0], events[^1]);
    }

    private sealed record WorkflowSpec(string Id, string Name, IReadOnlyList<ResolvedWorkflowParticipant> Participants);
    private sealed record Invocation(string Id, AgentRunContext Context);

    private sealed class TestSystem(IAgentRunner runner, Catalog catalog, AgentRunContextFactory contexts, FakeLlmFactory llmFactory, FakeWorkflowFactory workflowFactory)
    {
        public FakeLlmFactory LlmFactory { get; } = llmFactory;
        public FakeWorkflowFactory WorkflowFactory { get; } = workflowFactory;
        public async Task<List<AgentRunEvent>> RunAsync(string workflowId)
        {
            var definition = await catalog.GetRequiredAsync(new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, workflowId));
            var events = new List<AgentRunEvent>();
            await foreach (var item in runner.RunAsync(definition.Reference, new AgentRuntimeRunRequest { Messages = [new(AgentMessageRole.User, "go")] }, new AgentRuntimeCreationContext { Configuration = new AppChatConfiguration("model", []) }, contexts.CreateRoot(definition)))
                events.Add(item);
            return events;
        }
    }

    private sealed class Catalog(IEnumerable<WorkflowSpec> workflows) : IAgentDefinitionCatalog
    {
        private readonly Dictionary<string, AgentDefinitionDescriptor> definitions = workflows.SelectMany(static workflow => workflow.Participants.Append(WorkflowParticipant(workflow.Id, workflow.Name))).Select(static participant => new AgentDefinitionDescriptor { Reference = participant.Source is ReferencedParticipantSource source ? source.Reference : throw new InvalidOperationException(), Name = participant.DisplayName, RuntimeKind = participant.RuntimeKind, ModelRequirement = AgentModelRequirement.None }).GroupBy(static descriptor => $"{descriptor.Reference.Kind}:{descriptor.Reference.Id}").ToDictionary(static group => group.Key, static group => group.First());
        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>(definitions.Values.ToList());
        public Task<AgentDefinitionDescriptor?> FindAsync(AgentDefinitionReference reference, CancellationToken cancellationToken = default) => Task.FromResult(definitions.GetValueOrDefault($"{reference.Kind}:{reference.Id}"));
        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(AgentDefinitionReference reference, CancellationToken cancellationToken = default) => await FindAsync(reference, cancellationToken) ?? throw new KeyNotFoundException();
    }

    private sealed class FakeWorkflowFactory(IEnumerable<WorkflowSpec> workflows, IWorkflowParticipantInvoker invoker) : IWorkflowAgentRuntimeFactory
    {
        private readonly Dictionary<string, WorkflowSpec> workflowsById = workflows.ToDictionary(static workflow => workflow.Id);
        public List<string> CreatedWorkflowIds { get; } = [];
        public Task<IAgentRuntime> CreateAsync(string workflowId, AgentRuntimeCreationContext context, CancellationToken cancellationToken = default)
        {
            CreatedWorkflowIds.Add(workflowId);
            var spec = workflowsById[workflowId];
            IOrchestrationWorkflowDefinition definition = new SequentialWorkflowDefinition { Id = spec.Id, DisplayName = spec.Name, Participants = spec.Participants.Select(static participant => new WorkflowParticipantDefinition { Id = participant.ParticipantId, Role = participant.DisplayName, Source = new SavedDefinitionParticipantSource(((ReferencedParticipantSource)participant.Source).Reference) }).ToList(), ParticipantOrder = spec.Participants.Select(static participant => participant.ParticipantId).ToList() };
            return Task.FromResult<IAgentRuntime>(new WorkflowAgentRuntime(new AgentRuntimeDescriptor(spec.Id, spec.Name, string.Empty, AgentRuntimeKind.WorkflowAgent), definition, spec.Participants, [], context.Configuration, context, new UnsupportedHeadlessRunner(), invoker, NullLogger<WorkflowAgentRuntime>.Instance));
        }
    }

    private sealed class FakeLlmFactory : ILlmAgentRuntimeFactory
    {
        public List<Invocation> Invocations { get; } = [];
        public Task<IAgentRuntime> CreateAsync(string agentId, AgentRuntimeCreationContext context, CancellationToken cancellationToken = default) => Task.FromResult<IAgentRuntime>(new LeafRuntime(agentId, Invocations));
    }
    private sealed class LeafRuntime(string id, List<Invocation> invocations) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } = new(id, id, string.Empty, AgentRuntimeKind.LlmAgent);
        public async IAsyncEnumerable<AgentRunEvent> RunAsync(AgentRuntimeRunRequest request, AgentRunContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default) { invocations.Add(new Invocation(id, context)); await Task.Yield(); var message = new AgentOutputMessage(id, id); yield return new AgentRunCompleted(new AgentRunResult { FinalMessage = message, Messages = [message] }); }
    }
    private sealed class UnsupportedInlineFactory : IInlineLlmAgentRuntimeFactory { public IAgentRuntime Create(AgentRuntimeDescriptor descriptor, AgentTemplateDefinition agent, AgentRuntimeCreationContext context) => throw new NotSupportedException(); }
    private sealed class UnsupportedHeadlessRunner : IHeadlessWorkflowRunner { public Task<IHeadlessWorkflowSession> StartAsync(HeadlessWorkflowSessionStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
}
