using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class WorkflowParticipantUnificationTests
{
    [Fact]
    [Obsolete]
    public async Task NormalizeAsync_FromSavedAgentNameResolvesToCanonicalSavedDefinition()
    {
        var savedAgent = CreateAgent("Code Reviewer", "Review code.");
        var normalizer = new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService([savedAgent]));
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("reviewer")
            .Agent("reviewer", agent => agent
                .FromSavedAgent("Code Reviewer"))
            .Build();

        var normalized = await normalizer.NormalizeAsync(workflow);

        var participant = Assert.Single(normalized.Participants);
        var source = Assert.IsType<SavedDefinitionParticipantSource>(participant.Source);
        Assert.Equal(AgentDefinitionKind.SavedAgent, source.Reference.Kind);
        Assert.Equal(savedAgent.Id.ToString("D"), source.Reference.Id);
    }

    [Fact]
    public async Task NormalizeAsync_RejectsUnknownSavedAgentName()
    {
        var normalizer = new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService([]));
        var workflow = CreateSequentialWorkflow(new WorkflowParticipantDefinition
        {
            Id = "reviewer",
            Role = "Reviewer",
            Source = new SavedAgentNameParticipantSource("Missing Agent")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            normalizer.NormalizeAsync(workflow));

        Assert.Contains("Missing Agent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("was not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalizeAsync_RejectsAmbiguousSavedAgentName()
    {
        var normalizer = new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService(
        [
            CreateAgent("Code Reviewer", "First"),
            CreateAgent("Code Reviewer", "Second")
        ]));
        var workflow = CreateSequentialWorkflow(new WorkflowParticipantDefinition
        {
            Id = "reviewer",
            Role = "Reviewer",
            Source = new SavedAgentNameParticipantSource("Code Reviewer")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            normalizer.NormalizeAsync(workflow));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Obsolete]
    public async Task NormalizeAsync_RemovesLegacySourcesFromBoundary()
    {
        var savedAgent = CreateAgent("Saved", "Saved instructions");
        var normalizer = new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService([savedAgent]));
        var workflow = new SequentialWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            Participants =
            [
                new WorkflowParticipantDefinition
                {
                    Id = "by-source-name",
                    Role = "By Source Name",
                    Source = new SavedAgentNameParticipantSource("Saved")
                },
                new WorkflowParticipantDefinition
                {
                    Id = "by-agent-draft",
                    Role = "By Agent Draft",
                    AgentDraft = CreateAgent("Draft", "Draft instructions")
                },
                new WorkflowParticipantDefinition
                {
                    Id = "by-saved-template",
                    Role = "By Saved Template",
                    SavedAgentTemplate = new AgentWorkflowSavedAgentTemplate { SavedAgentName = "Saved" }
                }
            ],
            ParticipantOrder = ["by-source-name", "by-agent-draft", "by-saved-template"]
        };

        var normalized = await normalizer.NormalizeAsync(workflow);

        Assert.All(normalized.Participants, participant =>
        {
            Assert.IsNotType<SavedAgentNameParticipantSource>(participant.Source);
            Assert.Null(participant.AgentDraft);
            Assert.Null(participant.SavedAgentTemplate);
        });
        Assert.Contains(normalized.Participants, static participant =>
            participant.Id == "by-agent-draft" &&
            participant.Source is InlineAgentParticipantSource);
        Assert.Equal(2, normalized.Participants.Count(static participant =>
            participant.Source is SavedDefinitionParticipantSource));
    }

    [Fact]
    public async Task PreflightValidation_ReturnsStructuredProblemForUnknownSavedAgentName()
    {
        var workflowId = Guid.NewGuid();
        var validator = new WorkflowDefinitionPreflightValidator(
            new StubDependencyGraph([
                new AgentDefinitionProblem("Missing Agent was not found")
            ]));

        var problems = await validator.ValidateAsync(new AgentDefinitionReference(
            AgentDefinitionKind.SavedWorkflow,
            workflowId.ToString("D")));

        var problem = Assert.Single(problems);
        Assert.Contains("Missing Agent", problem.Message, StringComparison.Ordinal);
        Assert.Contains("was not found", problem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Obsolete]
    public async Task NormalizeAsync_RejectsParticipantWithMultipleExecutableSources()
    {
        var normalizer = new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService([]));
        var workflow = CreateSequentialWorkflow(new WorkflowParticipantDefinition
        {
            Id = "ambiguous",
            Role = "Ambiguous",
            Source = new InlineAgentParticipantSource(CreateAgent("Canonical", "canonical")),
            AgentDraft = CreateAgent("Legacy", "legacy")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            normalizer.NormalizeAsync(workflow));

        Assert.Equal(
            "Workflow participant 'ambiguous' defines more than one executable source.",
            exception.Message);
    }

    [Theory]
    [MemberData(nameof(AmbiguousParticipantSources))]
    public async Task NormalizeAsync_RejectsAllAmbiguousLegacySourceCombinations(
        WorkflowParticipantDefinition participant)
    {
        var savedAgent = CreateAgent("Saved", "saved");
        var normalizer = new LegacyWorkflowDefinitionNormalizer(new StubAgentTemplateService([savedAgent]));
        var workflow = CreateSequentialWorkflow(participant);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            normalizer.NormalizeAsync(workflow));

        Assert.Equal(
            $"Workflow participant '{participant.Id}' defines more than one executable source.",
            exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_MaterializesSavedAgentWithoutOverrides()
    {
        var savedAgent = CreateAgent("Original", "Original instructions");
        var resolver = CreateResolver([savedAgent]);
        var workflow = CreateSequentialWorkflow(new WorkflowParticipantDefinition
        {
            Id = "reviewer",
            Role = "Reviewer",
            Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                savedAgent.Id.ToString("D")))
        });

        var participant = Assert.Single(await resolver.ResolveAsync(workflow));

        var source = Assert.IsType<MaterializedLlmParticipantSource>(participant.Source);
        Assert.Equal("Original", source.Agent.AgentName);
        Assert.Equal("Original instructions", source.Agent.Content);
    }

    [Fact]
    public async Task RunAsync_SavedAgentOverridesReachMaterializedRuntime()
    {
        var savedAgent = CreateAgent("Original", "Original instructions");
        var resolver = CreateResolver([savedAgent]);
        var workflow = CreateSequentialWorkflow(new WorkflowParticipantDefinition
        {
            Id = "reviewer",
            Role = "Reviewer",
            Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                savedAgent.Id.ToString("D"))),
            Overrides = new WorkflowParticipantOverrides
            {
                DisplayName = "Reviewer",
                Llm = new LlmParticipantOverrides
                {
                    Instructions = "Overridden instructions"
                }
            }
        });
        var participants = await resolver.ResolveAsync(workflow);
        var inlineFactory = new RecordingInlineRuntimeFactory([
            new AgentRunCompleted(CreateResult("done"))
        ]);
        var runtime = CreateSequentialRuntime(workflow, participants, inlineFactory);

        var events = await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));

        Assert.IsType<AgentRunCompleted>(events.Last());
        var call = Assert.Single(inlineFactory.Calls);
        Assert.Equal("reviewer", call.Descriptor.Id);
        Assert.Equal("Reviewer", call.Descriptor.Name);
        Assert.Equal("Reviewer", call.Agent.AgentName);
        Assert.Equal("Overridden instructions", call.Agent.Content);
        Assert.Equal("Original instructions", savedAgent.Content);
    }

    [Fact]
    public async Task RunAsync_SavedAgentAppendedInstructionsReachMaterializedRuntime()
    {
        var savedAgent = CreateAgent("Original", "Base instructions");
        var resolver = CreateResolver([savedAgent]);
        var workflow = CreateSequentialWorkflow(new WorkflowParticipantDefinition
        {
            Id = "reviewer",
            Role = "Reviewer",
            Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                savedAgent.Id.ToString("D"))),
            Overrides = new WorkflowParticipantOverrides
            {
                Llm = new LlmParticipantOverrides
                {
                    AppendedInstructions = "Appended instructions"
                }
            }
        });
        var participants = await resolver.ResolveAsync(workflow);
        var inlineFactory = new RecordingInlineRuntimeFactory([
            new AgentRunCompleted(CreateResult("done"))
        ]);
        var runtime = CreateSequentialRuntime(workflow, participants, inlineFactory);

        await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));

        Assert.Equal(
            "Base instructions\n\nAppended instructions",
            Assert.Single(inlineFactory.Calls).Agent.Content);
    }

    [Theory]
    [MemberData(nameof(ProtocolViolationSequences))]
    public async Task WorkflowParticipantInvoker_AppliesProtocolValidationToReferencedAndMaterializedParticipants(
        IReadOnlyList<AgentRunEvent> runtimeEvents)
    {
        var request = CreateRequest();
        var creationContext = new AgentRuntimeCreationContext
        {
            Configuration = new AppChatConfiguration("model", [])
        };
        var protocolExecutor = new AgentRuntimeProtocolExecutor(
            NullLogger<AgentRuntimeProtocolExecutor>.Instance);

        var referencedExecutor = new WorkflowParticipantInvoker(
            new AgentRunContextFactory(),
            () => new AgentDefinitionExecutionDispatcher(
                new StubDefinitionCatalog([]),
                new AgentRunNestingValidator(new AgentRuntimeOptions()),
                new FixedRuntimeFactory(new StubRuntime(runtimeEvents)),
                protocolExecutor,
                NullLogger<AgentDefinitionExecutionDispatcher>.Instance),
                new RecordingInlineRuntimeFactory([]),
                new AgentRunNestingValidator(new AgentRuntimeOptions()),
                protocolExecutor);
        var inlineExecutor = new WorkflowParticipantInvoker(
            new AgentRunContextFactory(),
            () => new ThrowingAgentRunner(),
            new RecordingInlineRuntimeFactory(runtimeEvents),
            new AgentRunNestingValidator(new AgentRuntimeOptions()),
            protocolExecutor);

        await Assert.ThrowsAsync<AgentRuntimeProtocolException>(() =>
            CollectAsync(referencedExecutor.InvokeAsync(
                CreateReferencedParticipant(),
                request,
                creationContext,
                CreateContext())));
        await Assert.ThrowsAsync<AgentRuntimeProtocolException>(() =>
            CollectAsync(inlineExecutor.InvokeAsync(
                CreateMaterializedParticipant(),
                request,
                creationContext,
                CreateContext())));
    }

    [Obsolete]
    public static IEnumerable<object[]> AmbiguousParticipantSources()
    {
        var reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, Guid.NewGuid().ToString("D"));
        yield return
        [
            new WorkflowParticipantDefinition
            {
                Id = "source-agentdraft",
                Role = "A",
                Source = new SavedDefinitionParticipantSource(reference),
                AgentDraft = CreateAgent("Draft", "Draft")
            }
        ];
        yield return
        [
            new WorkflowParticipantDefinition
            {
                Id = "source-savedtemplate",
                Role = "A",
                Source = new SavedDefinitionParticipantSource(reference),
                SavedAgentTemplate = new AgentWorkflowSavedAgentTemplate { SavedAgentName = "Saved" }
            }
        ];
        yield return
        [
            new WorkflowParticipantDefinition
            {
                Id = "agentdraft-savedtemplate",
                Role = "A",
                AgentDraft = CreateAgent("Draft", "Draft"),
                SavedAgentTemplate = new AgentWorkflowSavedAgentTemplate { SavedAgentName = "Saved" }
            }
        ];
        yield return
        [
            new WorkflowParticipantDefinition
            {
                Id = "all",
                Role = "A",
                Source = new SavedDefinitionParticipantSource(reference),
                AgentDraft = CreateAgent("Draft", "Draft"),
                SavedAgentTemplate = new AgentWorkflowSavedAgentTemplate { SavedAgentName = "Saved" }
            }
        ];
    }

    public static IEnumerable<object[]> ProtocolViolationSequences()
    {
        yield return [Array.Empty<AgentRunEvent>()];
        yield return
        [
            new AgentRunEvent[]
            {
                new AgentRunCompleted(CreateResult("first")),
                new AgentTextDelta("late", "agent", "late")
            }
        ];
        yield return
        [
            new AgentRunEvent[]
            {
                new AgentRunCompleted(CreateResult("first")),
                new AgentRunCompleted(CreateResult("second"))
            }
        ];
    }

    private static WorkflowParticipantResolver CreateResolver(
        IReadOnlyCollection<AgentTemplateDefinition> agents) =>
        new(
            new StubAgentTemplateService(agents),
            new StubDefinitionCatalog(agents));

    private static SequentialWorkflowDefinition CreateSequentialWorkflow(
        WorkflowParticipantDefinition participant) =>
        new()
        {
            Id = "workflow",
            DisplayName = "Workflow",
            Participants = [participant],
            ParticipantOrder = [participant.Id]
        };

    private static WorkflowAgentRuntime CreateSequentialRuntime(
        SequentialWorkflowDefinition workflow,
        IReadOnlyList<ResolvedWorkflowParticipant> participants,
        RecordingInlineRuntimeFactory inlineFactory)
    {
        var configuration = new AppChatConfiguration("model", []);
        return new WorkflowAgentRuntime(
            new AgentRuntimeDescriptor("workflow", "Workflow", string.Empty, AgentRuntimeKind.WorkflowAgent),
            workflow,
            participants,
            [],
            configuration,
            new AgentRuntimeCreationContext
            {
                Configuration = configuration
            },
            new EmptyHeadlessWorkflowRunner(),
            new WorkflowParticipantInvoker(
                new AgentRunContextFactory(),
                () => new ThrowingAgentRunner(),
                inlineFactory,
                new AgentRunNestingValidator(new AgentRuntimeOptions()),
                new AgentRuntimeProtocolExecutor(NullLogger<AgentRuntimeProtocolExecutor>.Instance)),
            NullLogger<WorkflowAgentRuntime>.Instance);
    }

    private static ResolvedWorkflowParticipant CreateReferencedParticipant() =>
        new()
        {
            ParticipantId = "referenced",
            DisplayName = "Referenced",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            Source = new ReferencedParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedAgent,
                "agent"))
        };

    private static ResolvedWorkflowParticipant CreateMaterializedParticipant() =>
        new()
        {
            ParticipantId = "inline",
            DisplayName = "Inline",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            Source = new MaterializedLlmParticipantSource(CreateAgent("Inline", "Prompt"))
        };

    private static AgentTemplateDefinition CreateAgent(string name, string instructions) =>
        new()
        {
            Id = Guid.NewGuid(),
            AgentName = name,
            ShortName = name.ToLowerInvariant(),
            Content = instructions
        };

    private static AgentRuntimeRunRequest CreateRequest() =>
        new()
        {
            Messages = [new AgentInputMessage(AgentMessageRole.User, "go")]
        };

    private static AgentRunContext CreateContext() =>
        new()
        {
            RunId = Guid.NewGuid().ToString("N"),
            DefinitionStack =
            [
                new AgentRunFrame
                {
                    Definition = new AgentDefinitionReference(
                        AgentDefinitionKind.SavedWorkflow,
                        "workflow"),
                    DisplayName = "Workflow"
                }
            ]
        };

    private static AgentRunResult CreateResult(string content)
    {
        var message = new AgentOutputMessage("agent", content);
        return new AgentRunResult
        {
            FinalMessage = message,
            FinalMessageId = Guid.NewGuid().ToString("N"),
            Messages = [message]
        };
    }

    private static async Task<List<AgentRunEvent>> CollectAsync(
        IAsyncEnumerable<AgentRunEvent> events)
    {
        var result = new List<AgentRunEvent>();
        await foreach (var runEvent in events)
        {
            result.Add(runEvent);
        }

        return result;
    }

    private sealed class RecordingInlineRuntimeFactory(
        IReadOnlyList<AgentRunEvent> events) : IInlineLlmAgentRuntimeFactory
    {
        public List<Call> Calls { get; } = [];

        public IAgentRuntime Create(
            AgentRuntimeDescriptor descriptor,
            AgentTemplateDefinition agent,
            AgentRuntimeCreationContext context)
        {
            Calls.Add(new Call(descriptor, agent));
            return new StubRuntime(events);
        }

        public sealed record Call(
            AgentRuntimeDescriptor Descriptor,
            AgentTemplateDefinition Agent);
    }

    private sealed class FixedRuntimeFactory(IAgentRuntime runtime) : IAgentRuntimeFactory
    {
        public Task<IAgentRuntime> CreateAsync(
            AgentDefinitionReference reference,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(runtime);
    }

    private sealed class StubRuntime(IReadOnlyList<AgentRunEvent> events) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } =
            new("runtime", "Runtime", string.Empty, AgentRuntimeKind.LlmAgent);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var runEvent in events)
            {
                await Task.Yield();
                yield return runEvent;
            }
        }
    }

    private sealed class ThrowingAgentRunner : IAgentRunner, IAgentDefinitionExecutionDispatcher
    {
        public IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext runContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<AgentRunEvent> ExecuteAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyHeadlessWorkflowRunner : IHeadlessWorkflowRunner
    {
        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgentTemplateService(
        IReadOnlyCollection<AgentTemplateDefinition> agents) : IAgentTemplateService
    {
        public Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync() =>
            Task.FromResult(agents);

        public Task<AgentTemplateDefinition?> GetByIdAsync(Guid agentId) =>
            Task.FromResult(agents.FirstOrDefault(agent => agent.Id == agentId));

        public Task CreateAsync(AgentTemplateDefinition agentDescription) => throw new NotSupportedException();

        public Task UpdateAsync(AgentTemplateDefinition agentDescription) => throw new NotSupportedException();

        public Task DeleteAsync(Guid agentId) => throw new NotSupportedException();
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
                item.Reference.Kind == reference.Kind &&
                string.Equals(item.Reference.Id, reference.Id, StringComparison.OrdinalIgnoreCase));

        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            await FindAsync(reference, cancellationToken) ??
            new AgentDefinitionDescriptor
            {
                Reference = reference,
                Name = reference.Id,
                RuntimeKind = reference.Kind == AgentDefinitionKind.SavedWorkflow
                    ? AgentRuntimeKind.WorkflowAgent
                    : AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required
            };
    }

    private sealed class StubWorkflowDefinitionService(SavedWorkflowDefinition workflow) : IWorkflowDefinitionService
    {
        public Task<IReadOnlyCollection<SavedWorkflowDefinition>> GetAllAsync() =>
            Task.FromResult<IReadOnlyCollection<SavedWorkflowDefinition>>([workflow]);

        public Task<SavedWorkflowDefinition?> GetByIdAsync(Guid workflowId) =>
            Task.FromResult(workflow.Id == workflowId ? workflow : null);

        public Task CreateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task UpdateAsync(SavedWorkflowDefinition workflow) => throw new NotSupportedException();

        public Task DeleteAsync(Guid workflowId) => throw new NotSupportedException();
    }

    private sealed class StubWorkflowParticipantResolver : IWorkflowParticipantResolver
    {
        public Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
            IOrchestrationWorkflowDefinition workflow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResolvedWorkflowParticipant>>([]);
    }

    private sealed class StubDependencyGraph(
        IReadOnlyList<AgentDefinitionProblem> problems) : IAgentDefinitionDependencyGraph
    {
        public Task<AgentDefinitionDependencyAnalysis> AnalyzeAsync(
            AgentDefinitionReference root,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentDefinitionDependencyAnalysis
            {
                Root = root,
                Problems = problems
            });
    }
}
