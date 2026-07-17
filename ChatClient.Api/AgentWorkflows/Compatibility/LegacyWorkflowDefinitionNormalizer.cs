using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows.Compatibility;

public interface ILegacyWorkflowDefinitionNormalizer
{
    Task<IOrchestrationWorkflowDefinition> NormalizeAsync(
        IOrchestrationWorkflowDefinition definition,
        CancellationToken cancellationToken = default);
}

public sealed class LegacyWorkflowDefinitionNormalizer(
    IAgentTemplateService agentTemplateService) : ILegacyWorkflowDefinitionNormalizer
{
    public async Task<IOrchestrationWorkflowDefinition> NormalizeAsync(
        IOrchestrationWorkflowDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var savedAgents = await agentTemplateService.GetAllAsync();
        var participants = definition.Participants
            .Select(participant => NormalizeParticipant(participant, savedAgents))
            .ToList();

        return definition switch
        {
            AgentWorkflowDefinition handoff => new AgentWorkflowDefinition
            {
                Id = handoff.Id,
                DisplayName = handoff.DisplayName,
                Description = handoff.Description,
                StartParticipantId = handoff.StartParticipantId,
                Execution = CloneExecution(handoff.Execution),
                StartInputs = CloneStartInputs(handoff.StartInputs),
                Participants = participants,
                Handoffs = handoff.Handoffs.Select(static handoff => new AgentWorkflowHandoffDefinition
                {
                    FromParticipantId = handoff.FromParticipantId,
                    ToParticipantId = handoff.ToParticipantId,
                    Label = handoff.Label,
                    IsFallback = handoff.IsFallback
                }).ToList()
            },
            SequentialWorkflowDefinition sequential => new SequentialWorkflowDefinition
            {
                Id = sequential.Id,
                DisplayName = sequential.DisplayName,
                Description = sequential.Description,
                Execution = CloneExecution(sequential.Execution),
                StartInputs = CloneStartInputs(sequential.StartInputs),
                Participants = participants,
                ParticipantOrder = sequential.ParticipantOrder.ToList()
            },
            ConcurrentWorkflowDefinition concurrent => new ConcurrentWorkflowDefinition
            {
                Id = concurrent.Id,
                DisplayName = concurrent.DisplayName,
                Description = concurrent.Description,
                Execution = CloneExecution(concurrent.Execution),
                StartInputs = CloneStartInputs(concurrent.StartInputs),
                Participants = participants,
                ParticipantIds = concurrent.ParticipantIds.ToList(),
                Aggregation = new ConcurrentWorkflowAggregationDefinition
                {
                    Kind = concurrent.Aggregation.Kind
                }
            },
            GroupChatWorkflowDefinition groupChat => new GroupChatWorkflowDefinition
            {
                Id = groupChat.Id,
                DisplayName = groupChat.DisplayName,
                Description = groupChat.Description,
                Execution = CloneExecution(groupChat.Execution),
                StartInputs = CloneStartInputs(groupChat.StartInputs),
                Participants = participants,
                ParticipantIds = groupChat.ParticipantIds.ToList(),
                Manager = new GroupChatWorkflowManagerDefinition
                {
                    Kind = groupChat.Manager.Kind,
                    MaximumIterations = groupChat.Manager.MaximumIterations,
                    ImplementationKey = groupChat.Manager.ImplementationKey,
                    Program = groupChat.Manager.Program,
                    ProgramDisplayName = groupChat.Manager.ProgramDisplayName ?? groupChat.Manager.Program?.DisplayName
                }
            },
            _ => throw new InvalidOperationException(
                $"Workflow kind '{definition.Kind}' is not supported.")
        };
    }

    private static WorkflowParticipantDefinition NormalizeParticipant(
        WorkflowParticipantDefinition participant,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents)
    {
        var source = NormalizeSource(participant, savedAgents);
        return new WorkflowParticipantDefinition
        {
            Id = participant.Id,
            Role = participant.Role,
            Summary = participant.Summary,
            Source = source,
            Overrides = NormalizeOverrides(participant),
            CapabilityRequirements = participant.CapabilityRequirements
                .Select(static capability => new AgentWorkflowCapabilityRequirement
                {
                    Key = capability.Key,
                    DisplayName = capability.DisplayName,
                    Purpose = capability.Purpose,
                    Availability = capability.Availability,
                    AvailabilityNote = capability.AvailabilityNote
                })
                .ToList(),
            MaxTurnsPerSession = participant.MaxTurnsPerSession,
            MinAssistantTurnsBetweenTurns = participant.MinAssistantTurnsBetweenTurns
        };
    }

    private static WorkflowParticipantSource? NormalizeSource(
        WorkflowParticipantDefinition participant,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents)
    {
        var sourceCount =
            (participant.Source is not null ? 1 : 0) +
#pragma warning disable CS0618
            (participant.AgentDraft is not null ? 1 : 0) +
            (participant.SavedAgentTemplate is not null ? 1 : 0);
#pragma warning restore CS0618
        if (sourceCount > 1)
        {
            throw new InvalidOperationException(
                $"Workflow participant '{participant.Id}' defines more than one executable source.");
        }

        if (participant.Source is InlineAgentParticipantSource inline)
        {
            return new InlineAgentParticipantSource(inline.Agent.Clone());
        }

        if (participant.Source is SavedDefinitionParticipantSource saved)
        {
            return new SavedDefinitionParticipantSource(saved.Reference);
        }

        if (participant.Source is SavedAgentNameParticipantSource savedByName)
        {
            return ResolveSavedAgentName(participant, savedByName.SavedAgentName, savedAgents);
        }

#pragma warning disable CS0618
        if (participant.AgentDraft is not null)
        {
            return new InlineAgentParticipantSource(participant.AgentDraft.Clone());
        }

        if (participant.SavedAgentTemplate is not null)
        {
            return ResolveSavedAgentName(
                participant,
                participant.SavedAgentTemplate.SavedAgentName,
                savedAgents);
        }
#pragma warning restore CS0618

        return null;
    }

    private static SavedDefinitionParticipantSource ResolveSavedAgentName(
        WorkflowParticipantDefinition participant,
        string? savedAgentName,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents)
    {
        var matches = savedAgents
            .Where(savedAgent => string.Equals(
                savedAgent.AgentName,
                savedAgentName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var savedAgent = matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"Saved agent '{savedAgentName}' was not found for workflow participant '{participant.Id}'."),
            _ => throw new InvalidOperationException(
                $"Saved agent name '{savedAgentName}' is ambiguous for workflow participant '{participant.Id}'.")
        };

        return new SavedDefinitionParticipantSource(new AgentDefinitionReference(
            AgentDefinitionKind.SavedAgent,
            savedAgent.Id.ToString("D")));
    }

    private static WorkflowParticipantOverrides NormalizeOverrides(WorkflowParticipantDefinition participant)
    {
#pragma warning disable CS0618
        var legacy = participant.DraftOverrides;
#pragma warning restore CS0618
        var llm = participant.Overrides.Llm;
        var normalizedLlm = llm is null &&
                            (HasValue(legacy.AvatarText) ||
                             HasValue(legacy.Instructions) ||
                             HasValue(legacy.AppendedInstructions))
            ? new LlmParticipantOverrides
            {
                AvatarText = NormalizeOptional(legacy.AvatarText),
                Instructions = NormalizeOptional(legacy.Instructions),
                AppendedInstructions = NormalizeOptional(legacy.AppendedInstructions)
            }
            : llm;

        return new WorkflowParticipantOverrides
        {
            DisplayName = NormalizeOptional(participant.Overrides.DisplayName) ??
                          NormalizeOptional(legacy.AgentName),
            Summary = NormalizeOptional(participant.Overrides.Summary),
            Llm = normalizedLlm
        };
    }

    private static List<WorkflowStartInputDefinition> CloneStartInputs(
        IReadOnlyCollection<WorkflowStartInputDefinition> startInputs) =>
        startInputs.Select(static input => new WorkflowStartInputDefinition
        {
            Key = input.Key,
            DisplayName = input.DisplayName,
            Description = input.Description,
            Kind = input.Kind,
            IsRequired = input.IsRequired,
            Placeholder = input.Placeholder,
            DefaultValue = input.DefaultValue
        }).ToList();

    private static AgentWorkflowExecutionDefinition CloneExecution(
        AgentWorkflowExecutionDefinition execution) =>
        new()
        {
            Mode = execution.Mode,
            MaxAutomaticTurns = execution.MaxAutomaticTurns,
            CompletionPhase = execution.CompletionPhase,
            CompletionSummaryLabel = execution.CompletionSummaryLabel
        };

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
