using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Api.AgentWorkflows.GroupChat;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.TaskSessions;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class HeadlessRecursiveWorkflowExecutionIntegrationTests
{
    private static readonly string[] WorkflowKinds =
    [
        WorkflowDefinitionKinds.Sequential,
        WorkflowDefinitionKinds.Concurrent,
        WorkflowDefinitionKinds.GroupChat
    ];

    private static readonly string[] HeadlessKinds =
    [
        WorkflowDefinitionKinds.Handoff,
        WorkflowDefinitionKinds.Concurrent,
        WorkflowDefinitionKinds.GroupChat
    ];

    public static TheoryData<string> WorkflowKindCases() => TheoryDataFrom(WorkflowKinds);

    public static TheoryData<string> HeadlessWorkflowKinds() => TheoryDataFrom(HeadlessKinds);

    public static TheoryData<string, LeafEmissionMode> HeadlessWorkflowKindsAndLeafModes()
    {
        var data = new TheoryData<string, LeafEmissionMode>();
        foreach (var kind in HeadlessKinds)
        {
            data.Add(kind, LeafEmissionMode.DeltaMessageCompletedRunCompleted);
            data.Add(kind, LeafEmissionMode.MessageCompletedRunCompleted);
            data.Add(kind, LeafEmissionMode.RunCompletedOnly);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WorkflowKindCases))]
    public async Task RunAsync_NestedWorkflow_ExecutesNestedDefinitionThroughProductionRuntime(string kind)
    {
        var rootId = NewId();
        var nestedId = NewId();
        var leafAId = NewId();
        var leafBId = NewId();
        var leafCId = NewId();
        var leafDId = NewId();
        var system = TestSystem.Create(8,
            Workflow(
                kind,
                rootId,
                "Root workflow",
                AgentParticipant("a", "Leaf A", leafAId),
                WorkflowParticipant("nested", "Nested workflow", nestedId),
                AgentParticipant("d", "Leaf D", leafDId)),
            Workflow(
                kind,
                nestedId,
                "Nested workflow",
                AgentParticipant("b", "Leaf B", leafBId),
                AgentParticipant("c", "Leaf C", leafCId)),
            Agent(leafAId, "Leaf A", "leaf A final"),
            Agent(leafBId, "Leaf B", "leaf B final"),
            Agent(leafCId, "Leaf C", "leaf C final"),
            Agent(leafDId, "Leaf D", "leaf D final"));

        var events = await system.RunAsync(rootId, conversationId: "conversation-1");

        AssertValidAgentRunProtocol(events);
        if (kind != WorkflowDefinitionKinds.Sequential)
        {
            Assert.IsType<HeadlessWorkflowRunner>(system.HeadlessWorkflowRunner);
        }

        Assert.Equal([rootId, nestedId], system.CreatedWorkflowIds);
        Assert.Equal(
            ["Leaf A", "Leaf B", "Leaf C", "Leaf D"],
            system.LeafInvocations
                .Select(static item => item.Context.DefinitionStack[^1].DisplayName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal));
        Assert.All(system.LeafInvocations, static item => Assert.NotNull(item.Context.ParentRunId));
        Assert.All(system.LeafInvocations.Where(static item => item.Context.DefinitionStack[^1].DisplayName is "Leaf B" or "Leaf C"), static item =>
            Assert.Equal(["Root workflow", "Nested workflow", item.Context.DefinitionStack[^1].DisplayName], item.Context.DefinitionStack.Select(static frame => frame.DisplayName)));
        Assert.Equal(["conversation-1"], system.ObservedContexts.Select(static context => context.ConversationId).Distinct());
        Assert.All(system.ObservedContexts.Where(static context => context.ParentRunId is not null), static context => Assert.NotEqual(context.ParentRunId, context.RunId));
        Assert.Equal(system.ObservedContexts[0].RunId, system.ObservedContexts[1].ParentRunId);
        Assert.All(system.LeafInvocations.Where(static item => item.Context.DefinitionStack[^1].DisplayName is "Leaf B" or "Leaf C"), item =>
            Assert.Equal(system.ObservedContexts[1].RunId, item.Context.ParentRunId));

        var completed = Assert.IsType<AgentRunCompleted>(events[^1]);
        if (kind == WorkflowDefinitionKinds.GroupChat)
        {
            Assert.False(string.IsNullOrWhiteSpace(completed.Result.FinalMessage.Content));
        }
        else
        {
            AssertFinalContent(completed.Result.FinalMessage.Content, kind, "leaf D final");
            AssertSingleFinalText(events, kind, "leaf D final", requireEachCompletedMessage: kind != WorkflowDefinitionKinds.Concurrent);
        }
    }

    [Theory]
    [MemberData(nameof(HeadlessWorkflowKinds))]
    public async Task RunAsync_HeadlessDirectCycle_FailsBeforeNestedRuntimeStarts(string kind)
    {
        var workflowId = NewId();
        var system = TestSystem.Create(8,
            Workflow(kind, workflowId, "A", WorkflowParticipant("self", "A again", workflowId)));

        var events = await system.RunAsync(workflowId);

        AssertValidAgentRunProtocol(events);
        var failure = Assert.IsType<AgentRunFailed>(events[^1]);
        Assert.Equal("workflow_cycle_detected", failure.Error.Code);
        Assert.Empty(system.LeafInvocations);
        Assert.Equal([workflowId], system.CreatedWorkflowIds);
        Assert.Contains(workflowId, failure.Error.Metadata["definition.stack"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workflow_execution_failed", failure.Error.Code, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HeadlessWorkflowKinds))]
    public async Task RunAsync_HeadlessIndirectCycle_PreservesDefinitionStackAndParticipantMetadata(string kind)
    {
        var a = NewId();
        var b = NewId();
        var c = NewId();
        var system = TestSystem.Create(8,
            Workflow(kind, a, "A", WorkflowParticipant("to-b", "To B", b)),
            Workflow(kind, b, "B", WorkflowParticipant("to-c", "To C", c)),
            Workflow(kind, c, "C", WorkflowParticipant("to-a", "Back to A", a)));

        var events = await system.RunAsync(a);

        AssertValidAgentRunProtocol(events);
        var failure = Assert.IsType<AgentRunFailed>(events[^1]);
        Assert.Equal("workflow_cycle_detected", failure.Error.Code);
        Assert.Equal([a, b, c, a], StackIds(failure.Error));
        Assert.Equal("to-a", failure.Error.Metadata["participant.id"]);
        Assert.Equal("Back to A", failure.Error.Metadata["participant.name"]);
        Assert.Empty(system.LeafInvocations);
    }

    [Theory]
    [MemberData(nameof(HeadlessWorkflowKinds))]
    public async Task RunAsync_HeadlessNestingLimit_FailsBeforeFourthWorkflowRuntimeStarts(string kind)
    {
        var a = NewId();
        var b = NewId();
        var c = NewId();
        var d = NewId();
        var leafId = NewId();
        var system = TestSystem.Create(3,
            Workflow(kind, a, "A", WorkflowParticipant("to-b", "To B", b)),
            Workflow(kind, b, "B", WorkflowParticipant("to-c", "To C", c)),
            Workflow(kind, c, "C", WorkflowParticipant("to-d", "To D", d)),
            Workflow(kind, d, "D", AgentParticipant("leaf", "Leaf", leafId)),
            Agent(leafId, "Leaf", "unreachable"));

        var events = await system.RunAsync(a);

        AssertValidAgentRunProtocol(events);
        var failure = Assert.IsType<AgentRunFailed>(events[^1]);
        Assert.Equal("workflow_nesting_limit_exceeded", failure.Error.Code);
        Assert.Contains("3", failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal([a, b, c], system.CreatedWorkflowIds);
        Assert.Empty(system.LeafInvocations);
        Assert.Equal([a, b, c, d], StackIds(failure.Error));
    }

    [Theory]
    [MemberData(nameof(HeadlessWorkflowKinds))]
    public async Task RunAsync_HeadlessNestedLeafFailure_PreservesOriginalError(string kind)
    {
        var rootId = NewId();
        var nestedId = NewId();
        var leafId = NewId();
        var system = TestSystem.Create(8,
            Workflow(kind, rootId, "Root", WorkflowParticipant("nested", "Nested", nestedId)),
            Workflow(kind, nestedId, "Nested", AgentParticipant("leaf", "Leaf", leafId)),
            Agent(leafId, "Leaf", "unused", LeafEmissionMode.Failure));

        var events = await system.RunAsync(rootId);

        AssertValidAgentRunProtocol(events);
        var failure = Assert.IsType<AgentRunFailed>(events[^1]);
        Assert.Equal("leaf_test_failure", failure.Error.Code);
        Assert.True(failure.Error.IsRetryable);
        Assert.Equal("preserved", failure.Error.Metadata["test.marker"]);
    }

    [Theory]
    [MemberData(nameof(HeadlessWorkflowKindsAndLeafModes))]
    public async Task RunAsync_HeadlessNestedLeafFinalModes_EmitFinalTextOnce(string kind, LeafEmissionMode mode)
    {
        var rootId = NewId();
        var nestedId = NewId();
        var leafId = NewId();
        var system = TestSystem.Create(8,
            Workflow(kind, rootId, "Root", WorkflowParticipant("nested", "Nested", nestedId)),
            Workflow(kind, nestedId, "Nested", AgentParticipant("leaf", "Leaf", leafId)),
            Agent(leafId, "Leaf", "final once", mode));

        var events = await system.RunAsync(rootId);

        AssertValidAgentRunProtocol(events);
        var completed = Assert.IsType<AgentRunCompleted>(events[^1]);
        AssertFinalContent(completed.Result.FinalMessage.Content, kind, "final once");
        AssertSingleFinalText(events, kind, "final once");
    }

    private static WorkflowSpec Workflow(
        string kind,
        string id,
        string name,
        params WorkflowParticipantDefinition[] participants) =>
        new(id, name, BuildWorkflow(kind, id, name, participants));

    private static AgentSpec Agent(
        string id,
        string name,
        string finalText,
        LeafEmissionMode mode = LeafEmissionMode.RunCompletedOnly) =>
        new(id, name, finalText, mode);

    private static IOrchestrationWorkflowDefinition BuildWorkflow(
        string kind,
        string id,
        string name,
        IReadOnlyList<WorkflowParticipantDefinition> participants)
    {
        var execution = new AgentWorkflowExecutionDefinition();
        return kind switch
        {
            WorkflowDefinitionKinds.Sequential => new SequentialWorkflowDefinition
            {
                Id = id,
                DisplayName = name,
                Execution = execution,
                Participants = participants.ToList(),
                ParticipantOrder = participants.Select(static item => item.Id).ToList()
            },
            WorkflowDefinitionKinds.Handoff => new AgentWorkflowDefinition
            {
                Id = id,
                DisplayName = name,
                Execution = execution,
                Participants = participants.ToList(),
                StartParticipantId = participants[0].Id,
                Handoffs = participants.Zip(participants.Skip(1), static (from, to) => new AgentWorkflowHandoffDefinition
                {
                    FromParticipantId = from.Id,
                    ToParticipantId = to.Id,
                    Label = $"Continue to {to.Role}",
                    IsFallback = true
                }).ToList()
            },
            WorkflowDefinitionKinds.Concurrent => new ConcurrentWorkflowDefinition
            {
                Id = id,
                DisplayName = name,
                Execution = execution,
                Participants = participants.ToList(),
                ParticipantIds = participants.Select(static item => item.Id).ToList(),
                Aggregation = new ConcurrentWorkflowAggregationDefinition
                {
                    Kind = ConcurrentWorkflowAggregationKind.LastMessagePerAgent
                }
            },
            WorkflowDefinitionKinds.GroupChat => new GroupChatWorkflowDefinition
            {
                Id = id,
                DisplayName = name,
                Execution = execution,
                Participants = participants.ToList(),
                ParticipantIds = participants.Select(static item => item.Id).ToList(),
                Manager = CreateGroupChatManager(participants)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported workflow kind.")
        };
    }

    private static GroupChatWorkflowManagerDefinition CreateGroupChatManager(
        IReadOnlyList<WorkflowParticipantDefinition> participants)
    {
        if (participants.Count != 3)
        {
            return new GroupChatWorkflowManagerDefinition
            {
                Kind = GroupChatWorkflowManagerKind.RoundRobin,
                MaximumIterations = participants.Count
            };
        }

        return new GroupChatWorkflowManagerDefinition
        {
            Kind = GroupChatWorkflowManagerKind.Programmable,
            MaximumIterations = 4,
            Program = new GroupChatManagerProgram(context =>
                context.PriorAssistantSpeakerIds.Count switch
                {
                    0 => participants[0].Id,
                    1 => participants[1].Id,
                    _ => participants[^1].Id
                })
        };
    }

    private static WorkflowParticipantDefinition AgentParticipant(string id, string name, string agentId) =>
        Participant(id, name, AgentDefinitionKind.SavedAgent, agentId);

    private static WorkflowParticipantDefinition WorkflowParticipant(string id, string name, string workflowId) =>
        Participant(id, name, AgentDefinitionKind.SavedWorkflow, workflowId);

    private static WorkflowParticipantDefinition Participant(
        string id,
        string name,
        AgentDefinitionKind kind,
        string definitionId) =>
        new()
        {
            Id = id,
            Role = name,
            Overrides = new WorkflowParticipantOverrides
            {
                DisplayName = name
            },
            Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(kind, definitionId))
        };

    private static string NewId() => Guid.NewGuid().ToString("D");

    private static TheoryData<string> TheoryDataFrom(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
        {
            data.Add(value);
        }

        return data;
    }

    private static IReadOnlyList<string> StackIds(AgentRunError error) =>
        error.Metadata["definition.stack"]
            .Split(" > ")
            .Select(static frame => frame.Split(':')[1].Split('(')[0])
            .ToList();

    private static void AssertValidAgentRunProtocol(IReadOnlyList<AgentRunEvent> events)
    {
        var terminalEvents = events
            .Where(static item => item is AgentRunCompleted or AgentRunFailed)
            .ToList();

        Assert.Single(terminalEvents);
        Assert.Same(events[^1], terminalEvents[0]);
    }

    private static void AssertSingleFinalText(
        IReadOnlyList<AgentRunEvent> events,
        string kind,
        string finalText,
        bool requireEachCompletedMessage = true)
    {
        if (requireEachCompletedMessage)
        {
            foreach (var deltaGroup in events.OfType<AgentTextDelta>().GroupBy(static delta => delta.MessageId))
            {
                var deltaContent = string.Concat(deltaGroup.Select(static delta => delta.Text));
                AssertFinalContent(deltaContent, kind, finalText);
            }
            Assert.All(
                events.OfType<AgentMessageCompleted>(),
                completed => AssertFinalContent(completed.Message.Content, kind, finalText));
        }

        var runCompleted = Assert.IsType<AgentRunCompleted>(events[^1]);
        AssertFinalContent(runCompleted.Result.FinalMessage.Content, kind, finalText);
    }

    private static void AssertFinalContent(string actual, string kind, string expectedText)
    {
        if (kind is not (WorkflowDefinitionKinds.Concurrent or WorkflowDefinitionKinds.GroupChat))
        {
            Assert.Equal(expectedText, actual);
            return;
        }

        Assert.Contains(expectedText, actual, StringComparison.Ordinal);
    }

    private sealed record WorkflowSpec(string Id, string Name, IOrchestrationWorkflowDefinition Definition);

    private sealed record AgentSpec(string Id, string Name, string FinalText, LeafEmissionMode Mode);

    public enum LeafEmissionMode
    {
        DeltaMessageCompletedRunCompleted,
        MessageCompletedRunCompleted,
        RunCompletedOnly,
        Failure
    }

    private sealed class TestSystem
    {
        private readonly Catalog _catalog;
        private readonly AgentRunContextFactory _contextFactory;
        private readonly IAgentRunner _runner;

        private TestSystem(
            Catalog catalog,
            AgentRunContextFactory contextFactory,
            IAgentRunner runner,
            RecordingWorkflowAgentRuntimeFactory workflowFactory,
            IHeadlessWorkflowRunner headlessWorkflowRunner,
            FakeLlmFactory llmFactory)
        {
            _catalog = catalog;
            _contextFactory = contextFactory;
            _runner = runner;
            WorkflowFactory = workflowFactory;
            HeadlessWorkflowRunner = headlessWorkflowRunner;
            LlmFactory = llmFactory;
        }

        public RecordingWorkflowAgentRuntimeFactory WorkflowFactory { get; }

        public IHeadlessWorkflowRunner HeadlessWorkflowRunner { get; }

        public FakeLlmFactory LlmFactory { get; }

        public IReadOnlyList<string> CreatedWorkflowIds => WorkflowFactory.CreatedWorkflowIds;

        public IReadOnlyList<Invocation> LeafInvocations => LlmFactory.Invocations;

        public IReadOnlyList<AgentRunContext> ObservedContexts =>
            WorkflowFactory.CreatedContexts.Concat(LlmFactory.Invocations.Select(static item => item.Context)).ToList();

        public static TestSystem Create(
            int maximumDepth,
            params object[] definitions)
        {
            var workflows = definitions.OfType<WorkflowSpec>().ToArray();
            var agents = definitions.OfType<AgentSpec>().ToArray();
            var catalog = new Catalog(workflows, agents);
            var contextFactory = new AgentRunContextFactory();
            var nestingValidator = new AgentRunNestingValidator(new AgentRuntimeOptions
            {
                MaximumWorkflowNestingDepth = maximumDepth
            });
            var protocol = new AgentRuntimeProtocolExecutor(NullLogger<AgentRuntimeProtocolExecutor>.Instance);
            var llmFactory = new FakeLlmFactory(agents);
            IAgentRunner? runner = null;
            var participantInvoker = new WorkflowParticipantInvoker(
                contextFactory,
                () => runner!,
                new UnsupportedInlineFactory(),
                nestingValidator,
                protocol);
            var headlessRunner = CreateHeadlessWorkflowRunner();
            var workflowFactory = new RecordingWorkflowAgentRuntimeFactory(new WorkflowAgentRuntimeFactory(
                new InMemoryWorkflowDefinitionService(workflows),
                new PrebuiltWorkflowDefinitionCompiler(workflows),
                new LegacyWorkflowDefinitionNormalizer(new EmptyAgentTemplateService()),
                new ReferencedWorkflowParticipantResolver(catalog),
                new WorkflowParticipantRuntimeFactory(),
                headlessRunner,
                participantInvoker,
                NullLogger<WorkflowAgentRuntimeFactory>.Instance));
            runner = new AgentRunner(
                catalog,
                nestingValidator,
                new AgentRuntimeFactory(llmFactory, workflowFactory),
                protocol,
                NullLogger<AgentRunner>.Instance);
            return new TestSystem(catalog, contextFactory, runner, workflowFactory, headlessRunner, llmFactory);
        }

        public async Task<List<AgentRunEvent>> RunAsync(string workflowId, string? conversationId = null)
        {
            var definition = await _catalog.GetRequiredAsync(new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, workflowId));
            var events = new List<AgentRunEvent>();
            await foreach (var item in _runner.RunAsync(
                               definition.Reference,
                               new AgentRuntimeRunRequest { Messages = [new AgentInputMessage(AgentMessageRole.User, "go")] },
                               new AgentRuntimeCreationContext { Configuration = new AppChatConfiguration("test-model", []) },
                               _contextFactory.CreateRoot(definition, conversationId)))
            {
                events.Add(item);
            }

            return events;
        }

        private static HeadlessWorkflowRunner CreateHeadlessWorkflowRunner()
        {
            var binding = new McpServerSessionBinding();
            binding.Parameters[TaskSessionStore.DatabaseFileParameter] = "headless-recursive-in-memory";
            var taskSessionStore = new TaskSessionStore(
                new McpServerSessionContext(binding),
                new InMemoryTaskSessionRepository());
            var bootstrapper = new OrchestrationWorkflowSessionBootstrapper(
                NullLogger<OrchestrationWorkflowSessionBootstrapper>.Instance,
                new AcceptAllModelCapabilityService(),
                taskSessionStore,
                new MarkdownDocumentIntakeService(),
                null!);
            var eventStreamProcessor = new OrchestrationWorkflowEventStreamProcessor(
                new AgenticChatEngineStreamingBridge(),
                new HarnessResponseEventProjector(NullLogger<HarnessResponseEventProjector>.Instance));
            var passExecutor = new OrchestrationWorkflowPassExecutor(
                NullLogger<OrchestrationWorkflowPassExecutor>.Instance,
                eventStreamProcessor,
                [
                    new HandoffRuntimeWorkflowBuilder(),
                    new ConcurrentRuntimeWorkflowBuilder(),
                    new GroupChatRuntimeWorkflowBuilder(new GroupChatManagerRegistry([]))
                ]);
            var turnCoordinator = new OrchestrationWorkflowTurnCoordinator(
                NullLogger<OrchestrationWorkflowTurnCoordinator>.Instance,
                new WorkflowExecutionPolicy());
            return new HeadlessWorkflowRunner(
                bootstrapper,
                turnCoordinator,
                passExecutor,
                taskSessionStore,
                NullLogger<HeadlessWorkflowRunner>.Instance);
        }
    }

    private sealed class RecordingWorkflowAgentRuntimeFactory(IWorkflowAgentRuntimeFactory inner) : IWorkflowAgentRuntimeFactory
    {
        public List<string> CreatedWorkflowIds { get; } = [];

        public List<AgentRunContext> CreatedContexts { get; } = [];

        public async Task<IAgentRuntime> CreateAsync(
            string workflowId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default)
        {
            CreatedWorkflowIds.Add(workflowId);
            return new RecordingRuntime(await inner.CreateAsync(workflowId, context, cancellationToken), CreatedContexts);
        }
    }

    private sealed class RecordingRuntime(IAgentRuntime inner, List<AgentRunContext> contexts) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor => inner.Descriptor;

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            contexts.Add(context);
            await foreach (var item in inner.RunAsync(request, context, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private sealed class PrebuiltWorkflowDefinitionCompiler(IEnumerable<WorkflowSpec> workflows) : IWorkflowDefinitionCompiler
    {
        private readonly Dictionary<string, WorkflowSpec> _workflows = workflows.ToDictionary(static item => item.Id);

        public Task<CompiledWorkflowDefinition> CompileAsync(string sourceCode, CancellationToken cancellationToken = default)
        {
            var workflow = _workflows[sourceCode].Definition;
            return Task.FromResult(new CompiledWorkflowDefinition
            {
                Kind = workflow.Kind,
                WorkflowId = workflow.Id,
                DisplayName = workflow.DisplayName,
                Description = workflow.Description,
                Workflow = workflow,
                HandoffWorkflow = workflow as AgentWorkflowDefinition,
                ConcurrentWorkflow = workflow as ConcurrentWorkflowDefinition,
                GroupChatWorkflow = workflow as GroupChatWorkflowDefinition
            });
        }
    }

    private sealed class ReferencedWorkflowParticipantResolver(IAgentDefinitionCatalog catalog) : IWorkflowParticipantResolver
    {
        public async Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
            IOrchestrationWorkflowDefinition workflow,
            CancellationToken cancellationToken = default)
        {
            var resolved = new List<ResolvedWorkflowParticipant>();
            foreach (var participant in workflow.Participants)
            {
                var source = Assert.IsType<SavedDefinitionParticipantSource>(participant.Source);
                var descriptor = await catalog.GetRequiredAsync(source.Reference, cancellationToken);
                resolved.Add(new ResolvedWorkflowParticipant
                {
                    ParticipantId = participant.Id,
                    DisplayName = participant.Overrides.DisplayName ?? participant.Role,
                    Summary = participant.Summary,
                    RuntimeKind = descriptor.RuntimeKind,
                    Source = new ReferencedParticipantSource(source.Reference)
                });
            }

            return resolved;
        }
    }

    private sealed class Catalog(IEnumerable<WorkflowSpec> workflows, IEnumerable<AgentSpec> agents) : IAgentDefinitionCatalog
    {
        private readonly Dictionary<string, AgentDefinitionDescriptor> _definitions = workflows
            .Select(static workflow => new AgentDefinitionDescriptor
            {
                Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, workflow.Id),
                Name = workflow.Name,
                RuntimeKind = AgentRuntimeKind.WorkflowAgent,
                ModelRequirement = AgentModelRequirement.None
            })
            .Concat(agents.Select(static agent => new AgentDefinitionDescriptor
            {
                Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, agent.Id),
                Name = agent.Name,
                RuntimeKind = AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required
            }))
            .ToDictionary(static item => AgentDefinitionReferenceComparer.Instance.GetKey(item.Reference));

        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>(_definitions.Values.ToList());

        public Task<AgentDefinitionDescriptor?> FindAsync(AgentDefinitionReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(_definitions.GetValueOrDefault(AgentDefinitionReferenceComparer.Instance.GetKey(reference)));

        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(AgentDefinitionReference reference, CancellationToken cancellationToken = default) =>
            await FindAsync(reference, cancellationToken) ?? throw new KeyNotFoundException();
    }

    private sealed class FakeLlmFactory(IEnumerable<AgentSpec> agents) : ILlmAgentRuntimeFactory
    {
        private readonly Dictionary<string, AgentSpec> _agents = agents.ToDictionary(static item => item.Id);

        public List<Invocation> Invocations { get; } = [];

        public Task<IAgentRuntime> CreateAsync(
            string agentId,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAgentRuntime>(new LeafRuntime(_agents[agentId], Invocations));
    }

    private sealed record Invocation(string Id, AgentRunContext Context);

    private sealed class LeafRuntime(AgentSpec agent, List<Invocation> invocations) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } = new(agent.Id, agent.Name, string.Empty, AgentRuntimeKind.LlmAgent);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            invocations.Add(new Invocation(agent.Id, context));
            await Task.Yield();
            if (agent.Mode == LeafEmissionMode.Failure)
            {
                yield return new AgentRunFailed(new AgentRunError(
                    "leaf_test_failure",
                    "Leaf failed.",
                    true)
                {
                    Metadata = new Dictionary<string, string>
                    {
                        ["test.marker"] = "preserved"
                    }
                });
                yield break;
            }

            const string messageId = "leaf-message";
            if (agent.Mode == LeafEmissionMode.DeltaMessageCompletedRunCompleted)
            {
                yield return new AgentTextDelta(messageId, agent.Name, agent.FinalText);
            }

            if (agent.Mode is LeafEmissionMode.DeltaMessageCompletedRunCompleted or LeafEmissionMode.MessageCompletedRunCompleted)
            {
                yield return new AgentMessageCompleted(messageId, new AgentOutputMessage(agent.Name, agent.FinalText));
            }

            yield return new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage(agent.Name, agent.FinalText),
                FinalMessageId = messageId,
                Messages = [new AgentOutputMessage(agent.Name, agent.FinalText)]
            });
        }
    }

    private sealed class InMemoryWorkflowDefinitionService(IEnumerable<WorkflowSpec> workflows) : IWorkflowDefinitionService
    {
        private readonly Dictionary<Guid, SavedWorkflowDefinition> _workflows = workflows.ToDictionary(
            static item => Guid.Parse(item.Id),
            static item => new SavedWorkflowDefinition
            {
                Id = Guid.Parse(item.Id),
                Kind = item.Definition.Kind,
                WorkflowId = item.Definition.Id,
                DisplayName = item.Name,
                SourceCode = item.Id
            });

        public Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<SavedWorkflowDefinition>>(_workflows.Values.ToList());

        public Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId) =>
            Task.FromResult(_workflows.GetValueOrDefault(workflowId));

        public Task CreateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task UpdateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task DeleteAsync(Guid workflowId) => throw new NotSupportedException();
    }

    private sealed class AcceptAllModelCapabilityService : IModelCapabilityService
    {
        public Task EnsureModelSupportedByServerAsync(ServerModel model, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class EmptyAgentTemplateService : IAgentTemplateService
    {
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<AgentTemplateDefinition>>([]);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid templateId) =>
            Task.FromResult<AgentTemplateDefinition?>(null);

        public Task CreateAsync(AgentTemplateDefinition template) => throw new NotSupportedException();

        public Task UpdateAsync(AgentTemplateDefinition template) => throw new NotSupportedException();

        public Task DeleteAsync(Guid templateId) => throw new NotSupportedException();
    }

    private sealed class UnsupportedInlineFactory : IInlineLlmAgentRuntimeFactory
    {
        public IAgentRuntime Create(
            AgentRuntimeDescriptor descriptor,
            AgentTemplateDefinition agent,
            AgentRuntimeCreationContext context) =>
            throw new NotSupportedException("Headless integration tests use referenced saved agents.");
    }

    private sealed class InMemoryTaskSessionRepository : ITaskSessionRepository
    {
        private readonly Dictionary<string, Session> _sessions = [];

        public Task<TaskSessionSnapshot> CreateSessionAsync(string databaseFilePath, string sessionId, string? title, string? description, CancellationToken cancellationToken = default)
        {
            var session = new Session(sessionId, title, description);
            _sessions[sessionId] = session;
            return Task.FromResult(session.ToSnapshot());
        }

        public Task<TaskSessionSnapshot> GetSessionAsync(string databaseFilePath, string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions[sessionId].ToSnapshot());

        public Task<TaskSessionSnapshot> SetPhaseAsync(string databaseFilePath, string sessionId, string phase, CancellationToken cancellationToken = default)
        {
            _sessions[sessionId].Phase = phase;
            return Task.FromResult(_sessions[sessionId].ToSnapshot());
        }

        public Task<TaskSessionDocumentSnapshot> UpsertDocumentAsync(string databaseFilePath, string sessionId, string kind, string markdown, string? title, string? source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskSessionDocumentSnapshot> GetDocumentAsync(string databaseFilePath, string sessionId, string kind, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskSessionParameterSnapshot> UpsertParameterAsync(string databaseFilePath, string sessionId, string key, string valueKind, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskSessionParameterSnapshot> GetParameterAsync(string databaseFilePath, string sessionId, string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TaskSessionTurnSnapshot> AppendTurnAsync(string databaseFilePath, string sessionId, string role, string content, string? speakerId, CancellationToken cancellationToken = default)
        {
            var session = _sessions[sessionId];
            var turn = new TaskSessionTurnSnapshot(sessionId, session.Turns.Count + 1, role, speakerId, content, DateTime.UtcNow);
            session.Turns.Add(turn);
            return Task.FromResult(turn);
        }

        public Task<IReadOnlyList<TaskSessionTurnSnapshot>> ListTurnsAsync(string databaseFilePath, string sessionId, long afterSequence, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskSessionTurnSnapshot>>(_sessions[sessionId].Turns.Where(turn => turn.Sequence > afterSequence).Take(maxCount).ToList());

        public Task<TaskSessionSummarySnapshot> SaveSummaryAsync(string databaseFilePath, string sessionId, string label, string markdown, CancellationToken cancellationToken = default)
        {
            var session = _sessions[sessionId];
            var summary = new TaskSessionSummarySnapshot(sessionId, label, markdown, DateTime.UtcNow, DateTime.UtcNow);
            session.Summaries[label] = summary;
            return Task.FromResult(summary);
        }

        public Task<TaskSessionSummarySnapshot> GetSummaryAsync(string databaseFilePath, string sessionId, string label, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions[sessionId].Summaries[label]);

        private sealed class Session(string id, string? title, string? description)
        {
            public string? Phase { get; set; }

            public List<TaskSessionTurnSnapshot> Turns { get; } = [];

            public Dictionary<string, TaskSessionSummarySnapshot> Summaries { get; } = [];

            public TaskSessionSnapshot ToSnapshot() => new(
                id,
                title,
                description,
                Phase,
                "active",
                DateTime.UtcNow,
                DateTime.UtcNow,
                Turns.Count,
                [],
                [],
                Summaries.Values.Select(static summary => new TaskSessionSummaryInfo(summary.Label, summary.CreatedAtUtc, summary.UpdatedAtUtc)).ToList());
        }
    }
}
