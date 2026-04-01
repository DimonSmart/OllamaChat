using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.AgentWorkflows;

public sealed class WorkflowDefinitionBuilder
{
    private readonly string _id;
    private string _displayName;
    private string _description = string.Empty;
    private AgentWorkflowExecutionDefinition _execution = new();
    private readonly List<WorkflowStartInputDefinition> _startInputs = [];
    private readonly List<AgentWorkflowAgentDefinition> _agents = [];
    private HandoffConfigurationBuilder? _handoff;
    private GroupChatConfigurationBuilder? _groupChat;
    private SequentialConfigurationBuilder? _sequential;
    private ConcurrentConfigurationBuilder? _concurrent;

    private WorkflowDefinitionBuilder(string id, string displayName)
    {
        _id = RequireValue(id, nameof(id));
        _displayName = RequireValue(displayName, nameof(displayName));
    }

    public static WorkflowDefinitionBuilder New(string id, string displayName) =>
        new(id, displayName);

    public WorkflowDefinitionBuilder DisplayName(string displayName)
    {
        _displayName = RequireValue(displayName, nameof(displayName));
        return this;
    }

    public WorkflowDefinitionBuilder Description(string description)
    {
        _description = description?.Trim() ?? string.Empty;
        return this;
    }

    public WorkflowDefinitionBuilder RunInteractively()
    {
        _execution = new AgentWorkflowExecutionDefinition
        {
            Mode = AgentWorkflowExecutionMode.Interactive
        };
        return this;
    }

    public WorkflowDefinitionBuilder RunAutonomously(
        int maxAutomaticTurns,
        string completionPhase = "complete",
        string? completionSummaryLabel = null)
    {
        if (maxAutomaticTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAutomaticTurns),
                maxAutomaticTurns,
                "Automatic turn limit must be greater than zero.");
        }

        _execution = new AgentWorkflowExecutionDefinition
        {
            Mode = AgentWorkflowExecutionMode.Autonomous,
            MaxAutomaticTurns = maxAutomaticTurns,
            CompletionPhase = RequireValue(completionPhase, nameof(completionPhase)),
            CompletionSummaryLabel = NormalizeOptional(completionSummaryLabel)
        };
        return this;
    }

    public WorkflowDefinitionBuilder RequireDocument(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.MarkdownDocument, required: true, configure);

    public WorkflowDefinitionBuilder OptionalDocument(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.MarkdownDocument, required: false, configure);

    public WorkflowDefinitionBuilder RequireText(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Text, required: true, configure);

    public WorkflowDefinitionBuilder OptionalText(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Text, required: false, configure);

    public WorkflowDefinitionBuilder RequireNumber(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Number, required: true, configure);

    public WorkflowDefinitionBuilder OptionalNumber(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Number, required: false, configure);

    public WorkflowDefinitionBuilder RequireBoolean(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Boolean, required: true, configure);

    public WorkflowDefinitionBuilder OptionalBoolean(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Boolean, required: false, configure);

    public WorkflowDefinitionBuilder RequireJson(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Json, required: true, configure);

    public WorkflowDefinitionBuilder OptionalJson(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Json, required: false, configure);

    public WorkflowDefinitionBuilder Agent(string id, Action<WorkflowAgentBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkflowAgentBuilder(id);
        configure(builder);

        var agent = builder.Build();
        UpsertAgent(agent);
        return this;
    }

    public WorkflowDefinitionBuilder AgentFromSaved(
        string savedAgentName,
        Action<WorkflowAgentBuilder>? configure = null)
    {
        var builder = new WorkflowAgentBuilder(savedAgentName)
            .UseSavedTemplate(savedAgentName);
        configure?.Invoke(builder);

        var agent = builder.Build();
        UpsertAgent(agent);
        return this;
    }

    public WorkflowDefinitionBuilder UseHandoff(Action<HandoffConfigurationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = _handoff ?? CreateExclusiveConfiguration(new HandoffConfigurationBuilder(), _handoff);
        configure(builder);
        _handoff = builder;
        return this;
    }

    public WorkflowDefinitionBuilder UseGroupChat(Action<GroupChatConfigurationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = _groupChat ?? CreateExclusiveConfiguration(new GroupChatConfigurationBuilder(), _groupChat);
        configure(builder);
        _groupChat = builder;
        return this;
    }

    public WorkflowDefinitionBuilder UseSequential(Action<SequentialConfigurationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = _sequential ?? CreateExclusiveConfiguration(new SequentialConfigurationBuilder(), _sequential);
        configure(builder);
        _sequential = builder;
        return this;
    }

    public WorkflowDefinitionBuilder UseConcurrent(Action<ConcurrentConfigurationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = _concurrent ?? CreateExclusiveConfiguration(new ConcurrentConfigurationBuilder(), _concurrent);
        configure(builder);
        _concurrent = builder;
        return this;
    }

    public IOrchestrationWorkflowDefinition Build()
    {
        if (_agents.Count == 0)
        {
            throw new InvalidOperationException("Workflow must define at least one agent.");
        }

        var configuredKinds = CountConfiguredKinds();
        if (configuredKinds == 0)
        {
            throw new InvalidOperationException(
                "Workflow orchestration kind is required. Configure one of UseHandoff, UseGroupChat, UseSequential, or UseConcurrent.");
        }

        if (configuredKinds > 1)
        {
            throw new InvalidOperationException(
                "Workflow can only use one orchestration kind.");
        }

        if (_handoff is not null)
        {
            return BuildHandoffWorkflow(_handoff);
        }

        if (_groupChat is not null)
        {
            return BuildGroupChatWorkflow(_groupChat);
        }

        if (_sequential is not null)
        {
            return BuildSequentialWorkflow(_sequential);
        }

        return BuildConcurrentWorkflow(_concurrent!);
    }

    private WorkflowDefinitionBuilder AddStartInput(
        string key,
        string displayName,
        WorkflowStartInputKind kind,
        bool required,
        Action<WorkflowStartInputBuilder>? configure)
    {
        var builder = new WorkflowStartInputBuilder(key, displayName, kind, required);
        configure?.Invoke(builder);

        var input = builder.Build();
        UpsertStartInput(input);
        return this;
    }

    private AgentWorkflowDefinition BuildHandoffWorkflow(HandoffConfigurationBuilder handoff)
    {
        var agentIds = GetDefinedAgentIds();
        var startAgentId = handoff.StartAgentId;

        if (!agentIds.Contains(startAgentId))
        {
            throw new InvalidOperationException(
                $"Workflow start agent '{startAgentId}' is not defined.");
        }

        var missingHandoffAgent = handoff.Handoffs.FirstOrDefault(edge =>
            !agentIds.Contains(edge.FromAgentId) ||
            !agentIds.Contains(edge.ToAgentId));
        if (missingHandoffAgent is not null)
        {
            throw new InvalidOperationException(
                $"Workflow handoff '{missingHandoffAgent.FromAgentId} -> {missingHandoffAgent.ToAgentId}' references an undefined agent.");
        }

        return new AgentWorkflowDefinition
        {
            Id = _id,
            DisplayName = _displayName,
            Description = _description,
            StartAgentId = startAgentId,
            Execution = CloneExecution(_execution),
            StartInputs = CloneStartInputs(),
            Agents = CloneAgents(),
            Handoffs = handoff.Handoffs.ToList()
        };
    }

    private GroupChatWorkflowDefinition BuildGroupChatWorkflow(GroupChatConfigurationBuilder groupChat)
    {
        var agentIds = GetDefinedAgentIds();
        var participantAgentIds = groupChat.ParticipantAgentIds.Count == 0
            ? _agents.Select(static agent => agent.Id).ToList()
            : groupChat.ParticipantAgentIds.ToList();

        var missingParticipantAgentId = participantAgentIds.FirstOrDefault(agentId => !agentIds.Contains(agentId));
        if (missingParticipantAgentId is not null)
        {
            throw new InvalidOperationException(
                $"Group chat participant '{missingParticipantAgentId}' is not defined as an agent.");
        }

        if (groupChat.Manager.Kind == GroupChatWorkflowManagerKind.Custom &&
            string.IsNullOrWhiteSpace(groupChat.Manager.ImplementationKey))
        {
            throw new InvalidOperationException(
                "Custom group chat managers require an implementation key.");
        }

        return new GroupChatWorkflowDefinition
        {
            Id = _id,
            DisplayName = _displayName,
            Description = _description,
            Execution = CloneExecution(_execution),
            StartInputs = CloneStartInputs(),
            Agents = CloneAgents(),
            ParticipantAgentIds = participantAgentIds,
            Manager = CloneManager(groupChat.Manager)
        };
    }

    private SequentialWorkflowDefinition BuildSequentialWorkflow(SequentialConfigurationBuilder sequential)
    {
        var agentIds = GetDefinedAgentIds();
        var agentOrder = sequential.AgentOrder.Count == 0
            ? _agents.Select(static agent => agent.Id).ToList()
            : sequential.AgentOrder.ToList();

        var missingAgentId = agentOrder.FirstOrDefault(agentId => !agentIds.Contains(agentId));
        if (missingAgentId is not null)
        {
            throw new InvalidOperationException(
                $"Sequential workflow agent '{missingAgentId}' is not defined.");
        }

        return new SequentialWorkflowDefinition
        {
            Id = _id,
            DisplayName = _displayName,
            Description = _description,
            Execution = CloneExecution(_execution),
            StartInputs = CloneStartInputs(),
            Agents = CloneAgents(),
            AgentOrder = agentOrder
        };
    }

    private ConcurrentWorkflowDefinition BuildConcurrentWorkflow(ConcurrentConfigurationBuilder concurrent)
    {
        var agentIds = GetDefinedAgentIds();
        var participantAgentIds = concurrent.ParticipantAgentIds.Count == 0
            ? _agents.Select(static agent => agent.Id).ToList()
            : concurrent.ParticipantAgentIds.ToList();

        var missingAgentId = participantAgentIds.FirstOrDefault(agentId => !agentIds.Contains(agentId));
        if (missingAgentId is not null)
        {
            throw new InvalidOperationException(
                $"Concurrent workflow participant '{missingAgentId}' is not defined.");
        }

        return new ConcurrentWorkflowDefinition
        {
            Id = _id,
            DisplayName = _displayName,
            Description = _description,
            Execution = CloneExecution(_execution),
            StartInputs = CloneStartInputs(),
            Agents = CloneAgents(),
            ParticipantAgentIds = participantAgentIds,
            Aggregation = CloneAggregation(concurrent.Aggregation)
        };
    }

    private void UpsertStartInput(WorkflowStartInputDefinition input)
    {
        _startInputs.RemoveAll(existing =>
            string.Equals(existing.Key, input.Key, StringComparison.OrdinalIgnoreCase));
        _startInputs.Add(input);
    }

    private void UpsertAgent(AgentWorkflowAgentDefinition agent)
    {
        _agents.RemoveAll(existing =>
            string.Equals(existing.Id, agent.Id, StringComparison.OrdinalIgnoreCase));
        _agents.Add(agent);
    }

    private HashSet<string> GetDefinedAgentIds() =>
        _agents.Select(static agent => agent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private int CountConfiguredKinds() =>
        (_handoff is not null ? 1 : 0) +
        (_groupChat is not null ? 1 : 0) +
        (_sequential is not null ? 1 : 0) +
        (_concurrent is not null ? 1 : 0);

    private T CreateExclusiveConfiguration<T>(T builder, T? existing)
        where T : class
    {
        if (existing is not null)
        {
            return existing;
        }

        if (CountConfiguredKinds() > 0)
        {
            throw new InvalidOperationException(
                "Workflow orchestration kind is already configured.");
        }

        return builder;
    }

    private List<WorkflowStartInputDefinition> CloneStartInputs() =>
        _startInputs.Select(CloneStartInput).ToList();

    private List<AgentWorkflowAgentDefinition> CloneAgents() =>
        _agents.Select(CloneAgent).ToList();

    private static WorkflowStartInputDefinition CloneStartInput(WorkflowStartInputDefinition input) =>
        new()
        {
            Key = input.Key,
            DisplayName = input.DisplayName,
            Description = input.Description,
            Kind = input.Kind,
            IsRequired = input.IsRequired,
            Placeholder = input.Placeholder,
            DefaultValue = input.DefaultValue
        };

    private static AgentWorkflowAgentDefinition CloneAgent(AgentWorkflowAgentDefinition agent) =>
        new()
        {
            Id = agent.Id,
            Role = agent.Role,
            Summary = agent.Summary,
            AgentDraft = agent.AgentDraft is null
                ? null
                : AgentDescriptionFactory.CreateDraft(agent.AgentDraft),
            SavedAgentTemplate = agent.SavedAgentTemplate is null
                ? null
                : new AgentWorkflowSavedAgentTemplate
                {
                    SavedAgentName = agent.SavedAgentTemplate.SavedAgentName
                },
            DraftOverrides = new AgentWorkflowAgentDraftOverrides
            {
                AgentName = agent.DraftOverrides.AgentName,
                AvatarText = agent.DraftOverrides.AvatarText,
                Instructions = agent.DraftOverrides.Instructions,
                AppendedInstructions = agent.DraftOverrides.AppendedInstructions
            },
            CapabilityRequirements = agent.CapabilityRequirements
                .Select(capability => new AgentWorkflowCapabilityRequirement
                {
                    Key = capability.Key,
                    DisplayName = capability.DisplayName,
                    Purpose = capability.Purpose,
                    Availability = capability.Availability,
                    AvailabilityNote = capability.AvailabilityNote
                })
                .ToList(),
            MaxTurnsPerSession = agent.MaxTurnsPerSession,
            MinAssistantTurnsBetweenTurns = agent.MinAssistantTurnsBetweenTurns
        };

    private static AgentWorkflowExecutionDefinition CloneExecution(AgentWorkflowExecutionDefinition execution) =>
        new()
        {
            Mode = execution.Mode,
            MaxAutomaticTurns = execution.MaxAutomaticTurns,
            CompletionPhase = execution.CompletionPhase,
            CompletionSummaryLabel = execution.CompletionSummaryLabel
        };

    private static GroupChatWorkflowManagerDefinition CloneManager(GroupChatWorkflowManagerDefinition manager) =>
        new()
        {
            Kind = manager.Kind,
            MaximumIterations = manager.MaximumIterations,
            ImplementationKey = manager.ImplementationKey
        };

    private static ConcurrentWorkflowAggregationDefinition CloneAggregation(ConcurrentWorkflowAggregationDefinition aggregation) =>
        new()
        {
            Kind = aggregation.Kind
        };

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
