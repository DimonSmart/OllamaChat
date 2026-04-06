using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public interface IWorkflowAgentDraftMaterializer
{
    Task<IOrchestrationWorkflowDefinition> MaterializeAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default);

    Task<TWorkflow> MaterializeAsync<TWorkflow>(
        TWorkflow workflow,
        CancellationToken cancellationToken = default)
        where TWorkflow : class, IOrchestrationWorkflowDefinition;
}

public sealed class WorkflowAgentDraftMaterializer(
    IAgentTemplateService agentDescriptionService) : IWorkflowAgentDraftMaterializer
{
    private readonly IAgentTemplateService _agentDescriptionService = agentDescriptionService;

    public async Task<IOrchestrationWorkflowDefinition> MaterializeAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        cancellationToken.ThrowIfCancellationRequested();

        var savedAgents = await _agentDescriptionService.GetAllAsync();
        var materializedAgents = workflow.Agents
            .Select(agent => MaterializeAgent(agent, savedAgents))
            .ToList();
        ResolveInstructionTemplates(materializedAgents);

        return workflow switch
        {
            AgentWorkflowDefinition handoff => new AgentWorkflowDefinition
            {
                Id = handoff.Id,
                DisplayName = handoff.DisplayName,
                Description = handoff.Description,
                StartAgentId = handoff.StartAgentId,
                Execution = CloneExecution(handoff.Execution),
                StartInputs = CloneStartInputs(handoff.StartInputs),
                Agents = materializedAgents,
                Handoffs = CloneHandoffs(handoff.Handoffs)
            },
            SequentialWorkflowDefinition sequential => new SequentialWorkflowDefinition
            {
                Id = sequential.Id,
                DisplayName = sequential.DisplayName,
                Description = sequential.Description,
                Execution = CloneExecution(sequential.Execution),
                StartInputs = CloneStartInputs(sequential.StartInputs),
                Agents = materializedAgents,
                AgentOrder = sequential.AgentOrder.ToList()
            },
            ConcurrentWorkflowDefinition concurrent => new ConcurrentWorkflowDefinition
            {
                Id = concurrent.Id,
                DisplayName = concurrent.DisplayName,
                Description = concurrent.Description,
                Execution = CloneExecution(concurrent.Execution),
                StartInputs = CloneStartInputs(concurrent.StartInputs),
                Agents = materializedAgents,
                ParticipantAgentIds = concurrent.ParticipantAgentIds.ToList(),
                Aggregation = CloneAggregation(concurrent.Aggregation)
            },
            GroupChatWorkflowDefinition groupChat => new GroupChatWorkflowDefinition
            {
                Id = groupChat.Id,
                DisplayName = groupChat.DisplayName,
                Description = groupChat.Description,
                Execution = CloneExecution(groupChat.Execution),
                StartInputs = CloneStartInputs(groupChat.StartInputs),
                Agents = materializedAgents,
                ParticipantAgentIds = groupChat.ParticipantAgentIds.ToList(),
                Manager = CloneManager(groupChat.Manager)
            },
            _ => throw new InvalidOperationException(
                $"Workflow kind '{workflow.Kind}' is not supported by the materializer.")
        };
    }

    public async Task<TWorkflow> MaterializeAsync<TWorkflow>(
        TWorkflow workflow,
        CancellationToken cancellationToken = default)
        where TWorkflow : class, IOrchestrationWorkflowDefinition
    {
        var materialized = await MaterializeAsync((IOrchestrationWorkflowDefinition)workflow, cancellationToken);
        return materialized as TWorkflow
               ?? throw new InvalidOperationException(
                   $"Materialized workflow type mismatch. Expected '{typeof(TWorkflow).Name}', got '{materialized.GetType().Name}'.");
    }

    private static List<WorkflowStartInputDefinition> CloneStartInputs(
        IReadOnlyCollection<WorkflowStartInputDefinition> startInputs) =>
        startInputs.Select(startInput => new WorkflowStartInputDefinition
        {
            Key = startInput.Key,
            DisplayName = startInput.DisplayName,
            Description = startInput.Description,
            Kind = startInput.Kind,
            IsRequired = startInput.IsRequired,
            Placeholder = startInput.Placeholder,
            DefaultValue = startInput.DefaultValue
        }).ToList();

    private static List<AgentWorkflowHandoffDefinition> CloneHandoffs(
        IReadOnlyCollection<AgentWorkflowHandoffDefinition> handoffs) =>
        handoffs.Select(handoff => new AgentWorkflowHandoffDefinition
        {
            FromAgentId = handoff.FromAgentId,
            ToAgentId = handoff.ToAgentId,
            Label = handoff.Label,
            IsFallback = handoff.IsFallback
        }).ToList();

    private static ConcurrentWorkflowAggregationDefinition CloneAggregation(
        ConcurrentWorkflowAggregationDefinition aggregation) =>
        new()
        {
            Kind = aggregation.Kind
        };

    private static GroupChatWorkflowManagerDefinition CloneManager(
        GroupChatWorkflowManagerDefinition manager) =>
        new()
        {
            Kind = manager.Kind,
            MaximumIterations = manager.MaximumIterations,
            ImplementationKey = manager.ImplementationKey,
            Program = manager.Program,
            ProgramDisplayName = manager.ProgramDisplayName ?? manager.Program?.DisplayName
        };

    private static AgentWorkflowExecutionDefinition CloneExecution(
        AgentWorkflowExecutionDefinition execution) =>
        new()
        {
            Mode = execution.Mode,
            MaxAutomaticTurns = execution.MaxAutomaticTurns,
            CompletionPhase = execution.CompletionPhase,
            CompletionSummaryLabel = execution.CompletionSummaryLabel
        };

    private static void ResolveInstructionTemplates(List<AgentWorkflowAgentDefinition> agents)
    {
        var agentsById = agents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            if (agent.AgentDraft is null ||
                string.IsNullOrWhiteSpace(agent.AgentDraft.Content))
            {
                continue;
            }

            agent.AgentDraft.Content = WorkflowInstructionTemplateResolver.ResolveAgentReferences(
                agent.AgentDraft.Content,
                agent.Id,
                agentsById);
        }
    }

    private static AgentWorkflowAgentDefinition MaterializeAgent(
        AgentWorkflowAgentDefinition agent,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents)
    {
        var draft = ResolveBaseDraft(agent, savedAgents);
        ApplyOverrides(draft, agent.DraftOverrides);
        var summary = ResolveSummary(agent, draft);

        // Workflow slots need a stable runtime key and compact UI alias independent of the saved-agent name.
        draft.RuntimeAgentId = agent.Id;
        draft.ShortName = agent.Id;

        return new AgentWorkflowAgentDefinition
        {
            Id = agent.Id,
            Role = agent.Role,
            Summary = summary,
            AgentDraft = draft,
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
    }

    private static string ResolveSummary(
        AgentWorkflowAgentDefinition agent,
        AgentTemplateDefinition draft)
    {
        if (!string.IsNullOrWhiteSpace(agent.Summary))
        {
            return agent.Summary;
        }

        return agent.SavedAgentTemplate is null
            ? string.Empty
            : draft.Summary?.Trim() ?? string.Empty;
    }

    private static AgentTemplateDefinition ResolveBaseDraft(
        AgentWorkflowAgentDefinition agent,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents)
    {
        if (agent.AgentDraft is not null)
        {
            return agent.AgentDraft.Clone();
        }

        if (agent.SavedAgentTemplate is null)
        {
            throw new InvalidOperationException(
                $"Workflow agent '{agent.Id}' has no inline draft and no saved-agent template.");
        }

        var matches = savedAgents
            .Where(savedAgent => string.Equals(
                savedAgent.AgentName,
                agent.SavedAgentTemplate.SavedAgentName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0].Clone(),
            0 => throw new InvalidOperationException(
                $"Saved agent '{agent.SavedAgentTemplate.SavedAgentName}' was not found for workflow agent '{agent.Id}'."),
            _ => throw new InvalidOperationException(
                $"Saved agent name '{agent.SavedAgentTemplate.SavedAgentName}' is ambiguous for workflow agent '{agent.Id}'.")
        };
    }

    private static void ApplyOverrides(
        AgentTemplateDefinition draft,
        AgentWorkflowAgentDraftOverrides overrides)
    {
        if (!string.IsNullOrWhiteSpace(overrides.AgentName))
        {
            draft.AgentName = overrides.AgentName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.AvatarText))
        {
            draft.AvatarText = overrides.AvatarText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Instructions))
        {
            draft.Content = overrides.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.AppendedInstructions))
        {
            draft.Content = AppendInstructions(draft.Content, overrides.AppendedInstructions);
        }
    }

    private static string AppendInstructions(string baseInstructions, string appendedInstructions)
    {
        var normalizedBase = baseInstructions?.Trim() ?? string.Empty;
        var normalizedAppendix = appendedInstructions.Trim();

        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            return normalizedAppendix;
        }

        return $"{normalizedBase}\n\n{normalizedAppendix}";
    }
}
