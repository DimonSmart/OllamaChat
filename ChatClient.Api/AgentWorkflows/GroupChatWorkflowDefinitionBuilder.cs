using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.AgentWorkflows;

public sealed class GroupChatWorkflowDefinitionBuilder
{
    private readonly string _id;
    private string _displayName;
    private string _description = string.Empty;
    private AgentWorkflowExecutionDefinition _execution = new();
    private readonly List<WorkflowStartInputDefinition> _startInputs = [];
    private readonly List<AgentWorkflowAgentDefinition> _agents = [];
    private readonly List<string> _participantAgentIds = [];
    private GroupChatWorkflowManagerDefinition _manager = new();

    private GroupChatWorkflowDefinitionBuilder(string id, string displayName)
    {
        _id = RequireValue(id, nameof(id));
        _displayName = RequireValue(displayName, nameof(displayName));
    }

    public static GroupChatWorkflowDefinitionBuilder New(string id, string displayName) =>
        new(id, displayName);

    public GroupChatWorkflowDefinitionBuilder DisplayName(string displayName)
    {
        _displayName = RequireValue(displayName, nameof(displayName));
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder Description(string description)
    {
        _description = description?.Trim() ?? string.Empty;
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder RunInteractively()
    {
        _execution = new AgentWorkflowExecutionDefinition
        {
            Mode = AgentWorkflowExecutionMode.Interactive
        };
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder RunAutonomously(
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

    public GroupChatWorkflowDefinitionBuilder RequireDocument(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.MarkdownDocument, required: true, configure);

    public GroupChatWorkflowDefinitionBuilder OptionalDocument(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.MarkdownDocument, required: false, configure);

    public GroupChatWorkflowDefinitionBuilder RequireText(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Text, required: true, configure);

    public GroupChatWorkflowDefinitionBuilder OptionalText(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Text, required: false, configure);

    public GroupChatWorkflowDefinitionBuilder RequireNumber(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Number, required: true, configure);

    public GroupChatWorkflowDefinitionBuilder OptionalNumber(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Number, required: false, configure);

    public GroupChatWorkflowDefinitionBuilder RequireBoolean(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Boolean, required: true, configure);

    public GroupChatWorkflowDefinitionBuilder OptionalBoolean(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Boolean, required: false, configure);

    public GroupChatWorkflowDefinitionBuilder RequireJson(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Json, required: true, configure);

    public GroupChatWorkflowDefinitionBuilder OptionalJson(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Json, required: false, configure);

    public GroupChatWorkflowDefinitionBuilder Agent(
        string id,
        Action<WorkflowAgentBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkflowAgentBuilder(id);
        configure(builder);

        var agent = builder.Build();
        UpsertAgent(agent);
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder Participant(string agentId)
    {
        var normalizedAgentId = RequireValue(agentId, nameof(agentId));
        _participantAgentIds.RemoveAll(existing =>
            string.Equals(existing, normalizedAgentId, StringComparison.OrdinalIgnoreCase));
        _participantAgentIds.Add(normalizedAgentId);
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder Participants(params string[] agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        foreach (var agentId in agentIds)
        {
            Participant(agentId);
        }

        return this;
    }

    public GroupChatWorkflowDefinitionBuilder UseRoundRobinManager(int maximumIterations = 40)
    {
        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations must be greater than zero.");
        }

        _manager = new GroupChatWorkflowManagerDefinition
        {
            Kind = GroupChatWorkflowManagerKind.RoundRobin,
            MaximumIterations = maximumIterations
        };
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder UseCustomManager(
        string implementationKey,
        int maximumIterations = 40)
    {
        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumIterations),
                maximumIterations,
                "Maximum iterations must be greater than zero.");
        }

        _manager = new GroupChatWorkflowManagerDefinition
        {
            Kind = GroupChatWorkflowManagerKind.Custom,
            MaximumIterations = maximumIterations,
            ImplementationKey = RequireValue(implementationKey, nameof(implementationKey))
        };
        return this;
    }

    public GroupChatWorkflowDefinitionBuilder UseProgrammableManager(
        Action<ProgrammableGroupChatManagerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ProgrammableGroupChatManagerBuilder(
            _manager.Kind == GroupChatWorkflowManagerKind.Programmable
                ? _manager
                : null);
        configure(builder);
        _manager = builder.Build();
        return this;
    }

    public GroupChatWorkflowDefinition Build()
    {
        if (_agents.Count == 0)
        {
            throw new InvalidOperationException("Workflow must define at least one agent.");
        }

        var agentIds = _agents
            .Select(static agent => agent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var participantAgentIds = _participantAgentIds.Count == 0
            ? _agents.Select(static agent => agent.Id).ToList()
            : _participantAgentIds.ToList();

        var missingParticipantAgentId = participantAgentIds.FirstOrDefault(agentId => !agentIds.Contains(agentId));
        if (missingParticipantAgentId is not null)
        {
            throw new InvalidOperationException(
                $"Group chat participant '{missingParticipantAgentId}' is not defined as an agent.");
        }

        GroupChatWorkflowManagerValidator.Validate(_manager, participantAgentIds);

        WorkflowInstructionTemplateResolver.ValidateAgentReferences(_agents);

        return new GroupChatWorkflowDefinition
        {
            Id = _id,
            DisplayName = _displayName,
            Description = _description,
            Execution = _execution,
            StartInputs = _startInputs.ToList(),
            Agents = _agents.ToList(),
            ParticipantAgentIds = participantAgentIds,
            Manager = _manager
        };
    }

    private GroupChatWorkflowDefinitionBuilder AddStartInput(
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
