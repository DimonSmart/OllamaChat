using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public sealed class HandoffWorkflowDefinitionBuilder
{
    private readonly string _id;
    private string _displayName;
    private string _description = string.Empty;
    private string? _startAgentId;
    private AgentWorkflowExecutionDefinition _execution = new();
    private readonly List<WorkflowStartInputDefinition> _startInputs = [];
    private readonly List<AgentWorkflowAgentDefinition> _agents = [];
    private readonly List<AgentWorkflowHandoffDefinition> _handoffs = [];

    private HandoffWorkflowDefinitionBuilder(string id, string displayName)
    {
        _id = RequireValue(id, nameof(id));
        _displayName = RequireValue(displayName, nameof(displayName));
    }

    public static HandoffWorkflowDefinitionBuilder New(string id, string displayName) =>
        new(id, displayName);

    public HandoffWorkflowDefinitionBuilder DisplayName(string displayName)
    {
        _displayName = RequireValue(displayName, nameof(displayName));
        return this;
    }

    public HandoffWorkflowDefinitionBuilder Description(string description)
    {
        _description = description?.Trim() ?? string.Empty;
        return this;
    }

    public HandoffWorkflowDefinitionBuilder StartWith(string agentId)
    {
        _startAgentId = RequireValue(agentId, nameof(agentId));
        return this;
    }

    public HandoffWorkflowDefinitionBuilder RunInteractively()
    {
        _execution = new AgentWorkflowExecutionDefinition
        {
            Mode = AgentWorkflowExecutionMode.Interactive
        };
        return this;
    }

    public HandoffWorkflowDefinitionBuilder RunAutonomously(
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

    public HandoffWorkflowDefinitionBuilder RequireDocument(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.MarkdownDocument, required: true, configure);

    public HandoffWorkflowDefinitionBuilder OptionalDocument(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.MarkdownDocument, required: false, configure);

    public HandoffWorkflowDefinitionBuilder RequireText(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Text, required: true, configure);

    public HandoffWorkflowDefinitionBuilder OptionalText(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Text, required: false, configure);

    public HandoffWorkflowDefinitionBuilder RequireNumber(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Number, required: true, configure);

    public HandoffWorkflowDefinitionBuilder OptionalNumber(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Number, required: false, configure);

    public HandoffWorkflowDefinitionBuilder RequireBoolean(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Boolean, required: true, configure);

    public HandoffWorkflowDefinitionBuilder OptionalBoolean(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Boolean, required: false, configure);

    public HandoffWorkflowDefinitionBuilder RequireJson(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Json, required: true, configure);

    public HandoffWorkflowDefinitionBuilder OptionalJson(
        string key,
        string displayName,
        Action<WorkflowStartInputBuilder>? configure = null) =>
        AddStartInput(key, displayName, WorkflowStartInputKind.Json, required: false, configure);

    public HandoffWorkflowDefinitionBuilder Agent(string id, Action<WorkflowAgentBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkflowAgentBuilder(id);
        configure(builder);

        var agent = builder.Build();
        UpsertAgent(agent);
        return this;
    }

    public HandoffWorkflowDefinitionBuilder Handoff(string fromAgentId, string toAgentId, string label)
    {
        _handoffs.Add(new AgentWorkflowHandoffDefinition
        {
            FromAgentId = RequireValue(fromAgentId, nameof(fromAgentId)),
            ToAgentId = RequireValue(toAgentId, nameof(toAgentId)),
            Label = RequireValue(label, nameof(label)),
            IsFallback = false
        });
        return this;
    }

    public HandoffWorkflowDefinitionBuilder Fallback(
        string fromAgentId,
        string toAgentId,
        string label = "fallback")
    {
        _handoffs.Add(new AgentWorkflowHandoffDefinition
        {
            FromAgentId = RequireValue(fromAgentId, nameof(fromAgentId)),
            ToAgentId = RequireValue(toAgentId, nameof(toAgentId)),
            Label = RequireValue(label, nameof(label)),
            IsFallback = true
        });
        return this;
    }

    public AgentWorkflowDefinition Build()
    {
        if (string.IsNullOrWhiteSpace(_startAgentId))
        {
            throw new InvalidOperationException("Workflow start agent is required.");
        }

        if (_agents.Count == 0)
        {
            throw new InvalidOperationException("Workflow must define at least one agent.");
        }

        var agentIds = _agents
            .Select(static agent => agent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!agentIds.Contains(_startAgentId))
        {
            throw new InvalidOperationException(
                $"Workflow start agent '{_startAgentId}' is not defined.");
        }

        var missingHandoffAgent = _handoffs.FirstOrDefault(handoff =>
            !agentIds.Contains(handoff.FromAgentId) ||
            !agentIds.Contains(handoff.ToAgentId));
        if (missingHandoffAgent is not null)
        {
            throw new InvalidOperationException(
                $"Workflow handoff '{missingHandoffAgent.FromAgentId} -> {missingHandoffAgent.ToAgentId}' references an undefined agent.");
        }

        WorkflowInstructionTemplateResolver.ValidateAgentReferences(_agents);

        return new AgentWorkflowDefinition
        {
            Id = _id,
            DisplayName = _displayName,
            Description = _description,
            StartAgentId = _startAgentId,
            Execution = _execution,
            StartInputs = _startInputs.ToList(),
            Agents = _agents.ToList(),
            Handoffs = _handoffs.ToList()
        };
    }

    private HandoffWorkflowDefinitionBuilder AddStartInput(
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

public sealed class WorkflowStartInputBuilder
{
    private readonly string _key;
    private readonly string _displayName;
    private readonly WorkflowStartInputKind _kind;
    private bool _isRequired;
    private string _description = string.Empty;
    private string? _placeholder;
    private string? _defaultValue;

    internal WorkflowStartInputBuilder(
        string key,
        string displayName,
        WorkflowStartInputKind kind,
        bool required)
    {
        _key = RequireValue(key, nameof(key));
        _displayName = RequireValue(displayName, nameof(displayName));
        _kind = kind;
        _isRequired = required;
    }

    public WorkflowStartInputBuilder Description(string description)
    {
        _description = description?.Trim() ?? string.Empty;
        return this;
    }

    public WorkflowStartInputBuilder Placeholder(string? placeholder)
    {
        _placeholder = NormalizeOptional(placeholder);
        return this;
    }

    public WorkflowStartInputBuilder DefaultValue(string? defaultValue)
    {
        _defaultValue = NormalizeOptional(defaultValue);
        return this;
    }

    public WorkflowStartInputBuilder Required()
    {
        _isRequired = true;
        return this;
    }

    public WorkflowStartInputBuilder Optional()
    {
        _isRequired = false;
        return this;
    }

    internal WorkflowStartInputDefinition Build() =>
        new()
        {
            Key = _key,
            DisplayName = _displayName,
            Description = _description,
            Kind = _kind,
            IsRequired = _isRequired,
            Placeholder = _placeholder,
            DefaultValue = _defaultValue
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

public sealed class WorkflowAgentBuilder
{
    private readonly string _id;
    private string _role = string.Empty;
    private string _summary = string.Empty;
    private AgentTemplateDefinition? _agentDraft;
    private string? _savedAgentTemplateName;
    private string? _overrideAgentName;
    private string? _overrideAvatarText;
    private string? _overrideInstructions;
    private string? _appendedInstructions;
    private readonly List<AgentWorkflowCapabilityRequirement> _capabilities = [];
    private int _maxTurnsPerSession;
    private int _minAssistantTurnsBetweenTurns;

    internal WorkflowAgentBuilder(string id)
    {
        _id = RequireValue(id, nameof(id));
    }

    public WorkflowAgentBuilder Role(string role)
    {
        _role = RequireValue(role, nameof(role));
        return this;
    }

    public WorkflowAgentBuilder Summary(string summary)
    {
        _summary = summary?.Trim() ?? string.Empty;
        return this;
    }

    public WorkflowAgentBuilder UseDraft(AgentTemplateDefinition draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _agentDraft = draft;
        return this;
    }

    public WorkflowAgentBuilder FromSavedAgent(string savedAgentName)
    {
        _savedAgentTemplateName = RequireValue(savedAgentName, nameof(savedAgentName));
        return this;
    }

    public WorkflowAgentBuilder OverrideName(string agentName)
    {
        _overrideAgentName = RequireValue(agentName, nameof(agentName));
        return this;
    }

    public WorkflowAgentBuilder OverrideAvatarText(string avatarText)
    {
        _overrideAvatarText = RequireValue(avatarText, nameof(avatarText));
        return this;
    }

    public WorkflowAgentBuilder OverrideInstructions(string instructions)
    {
        _overrideInstructions = RequireValue(instructions, nameof(instructions));
        return this;
    }

    public WorkflowAgentBuilder AppendInstructions(string instructions)
    {
        _appendedInstructions = RequireValue(instructions, nameof(instructions));
        return this;
    }

    public WorkflowAgentBuilder MaxTurnsPerSession(int maxTurnsPerSession)
    {
        if (maxTurnsPerSession <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTurnsPerSession),
                maxTurnsPerSession,
                "Per-agent turn limit must be greater than zero.");
        }

        _maxTurnsPerSession = maxTurnsPerSession;
        return this;
    }

    public WorkflowAgentBuilder MinAssistantTurnsBetweenTurns(int minAssistantTurnsBetweenTurns)
    {
        if (minAssistantTurnsBetweenTurns < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minAssistantTurnsBetweenTurns),
                minAssistantTurnsBetweenTurns,
                "Minimum assistant turns between repeated turns cannot be negative.");
        }

        _minAssistantTurnsBetweenTurns = minAssistantTurnsBetweenTurns;
        return this;
    }

    public WorkflowAgentBuilder Capability(
        string key,
        string displayName,
        Action<WorkflowCapabilityRequirementBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkflowCapabilityRequirementBuilder(key, displayName);
        configure(builder);
        _capabilities.Add(builder.Build());
        return this;
    }

    internal AgentWorkflowAgentDefinition Build()
    {
        var usesSavedTemplate = !string.IsNullOrWhiteSpace(_savedAgentTemplateName);
        if (_agentDraft is not null && usesSavedTemplate)
        {
            throw new InvalidOperationException(
                $"Workflow agent '{_id}' cannot use both an inline draft and a saved-agent template.");
        }

        if (_agentDraft is null && !usesSavedTemplate)
        {
            throw new InvalidOperationException(
                $"Workflow agent '{_id}' requires either an inline agent draft or a saved-agent template.");
        }

        if (!string.IsNullOrWhiteSpace(_overrideInstructions) &&
            !string.IsNullOrWhiteSpace(_appendedInstructions))
        {
            throw new InvalidOperationException(
                $"Workflow agent '{_id}' cannot use both OverrideInstructions and AppendInstructions.");
        }

        if (string.IsNullOrWhiteSpace(_role))
        {
            if (usesSavedTemplate)
            {
                _role = _overrideAgentName ?? _savedAgentTemplateName ?? "Saved agent";
            }
            else
            {
                throw new InvalidOperationException($"Workflow agent '{_id}' requires a role.");
            }
        }

        return new AgentWorkflowAgentDefinition
        {
            Id = _id,
            Role = _role,
            Summary = _summary,
            AgentDraft = _agentDraft,
            SavedAgentTemplate = usesSavedTemplate
                ? new AgentWorkflowSavedAgentTemplate
                {
                    SavedAgentName = _savedAgentTemplateName!
                }
                : null,
            DraftOverrides = new AgentWorkflowAgentDraftOverrides
            {
                AgentName = _overrideAgentName,
                AvatarText = _overrideAvatarText,
                Instructions = _overrideInstructions,
                AppendedInstructions = _appendedInstructions
            },
            CapabilityRequirements = _capabilities.ToList(),
            MaxTurnsPerSession = _maxTurnsPerSession,
            MinAssistantTurnsBetweenTurns = _minAssistantTurnsBetweenTurns
        };
    }

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}

public sealed class WorkflowCapabilityRequirementBuilder
{
    private readonly string _key;
    private readonly string _displayName;
    private string _purpose = string.Empty;
    private AgentWorkflowCapabilityAvailability _availability;
    private string _availabilityNote = string.Empty;

    internal WorkflowCapabilityRequirementBuilder(string key, string displayName)
    {
        _key = RequireValue(key, nameof(key));
        _displayName = RequireValue(displayName, nameof(displayName));
    }

    public WorkflowCapabilityRequirementBuilder Purpose(string purpose)
    {
        _purpose = purpose?.Trim() ?? string.Empty;
        return this;
    }

    public WorkflowCapabilityRequirementBuilder Availability(
        AgentWorkflowCapabilityAvailability availability)
    {
        _availability = availability;
        return this;
    }

    public WorkflowCapabilityRequirementBuilder AvailabilityNote(string note)
    {
        _availabilityNote = note?.Trim() ?? string.Empty;
        return this;
    }

    internal AgentWorkflowCapabilityRequirement Build() =>
        new()
        {
            Key = _key,
            DisplayName = _displayName,
            Purpose = _purpose,
            Availability = _availability,
            AvailabilityNote = _availabilityNote
        };

    private static string RequireValue(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", paramName);
        }

        return value.Trim();
    }
}
