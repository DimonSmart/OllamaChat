using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public interface IWorkflowParticipantResolver
{
    Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default);
}

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

public sealed class WorkflowParticipantResolver(
    IAgentTemplateService agentDescriptionService,
    IAgentDefinitionCatalog definitionCatalog) : IWorkflowParticipantResolver
{
    private readonly IAgentTemplateService _agentDescriptionService = agentDescriptionService;
    private readonly IAgentDefinitionCatalog _definitionCatalog = definitionCatalog;

    public async Task<IReadOnlyList<ResolvedWorkflowParticipant>> ResolveAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWorkflowShape(workflow);

        var savedAgents = await _agentDescriptionService.GetAllAsync();
        var resolved = new List<ResolvedWorkflowParticipant>();
        foreach (var participant in workflow.Participants)
        {
            resolved.Add(await ResolveParticipantAsync(
                participant,
                savedAgents,
                cancellationToken));
        }

        ResolveInstructionTemplates(resolved);
        return resolved;
    }

    private static void ValidateWorkflowShape(IOrchestrationWorkflowDefinition workflow)
    {
        if (workflow.Participants.Count == 0)
        {
            throw new InvalidOperationException("Workflow must define at least one participant.");
        }

        var duplicateParticipantId = workflow.Participants
            .GroupBy(static participant => participant.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateParticipantId))
        {
            throw new InvalidOperationException(
                $"Workflow contains duplicate participant id '{duplicateParticipantId}'.");
        }

        var duplicateStartInput = workflow.StartInputs
            .GroupBy(static input => input.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateStartInput))
        {
            throw new InvalidOperationException(
                $"Workflow contains duplicate start input key '{duplicateStartInput}'.");
        }

        foreach (var participant in workflow.Participants)
        {
            if (string.IsNullOrWhiteSpace(participant.Id))
            {
                throw new InvalidOperationException("Workflow participant id is required.");
            }

            if (participant.Source is null)
            {
                throw new InvalidOperationException(
                    $"Workflow participant '{participant.Id}' has no executable source.");
            }

            if (participant.Source is SavedDefinitionParticipantSource saved &&
                string.IsNullOrWhiteSpace(saved.Reference.Id))
            {
                throw new InvalidOperationException(
                    $"Workflow participant '{participant.Id}' references a saved definition without an id.");
            }
        }

        if (workflow is SequentialWorkflowDefinition sequential)
        {
            ValidateSequentialWorkflow(sequential);
        }
    }

    private static void ValidateSequentialWorkflow(SequentialWorkflowDefinition workflow)
    {
        if (workflow.ParticipantOrder.Count == 0)
        {
            throw new InvalidOperationException("Sequential workflow must define at least one ordered participant.");
        }

        var participantIds = workflow.Participants
            .Select(static participant => participant.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var participantId in workflow.ParticipantOrder)
        {
            if (!participantIds.Contains(participantId))
            {
                throw new InvalidOperationException(
                    $"Sequential workflow participant '{participantId}' is not defined.");
            }
        }

        var repeatedParticipantId = workflow.ParticipantOrder
            .GroupBy(static participantId => participantId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (!string.IsNullOrWhiteSpace(repeatedParticipantId))
        {
            throw new InvalidOperationException(
                $"Sequential workflow participant '{repeatedParticipantId}' is ordered more than once.");
        }
    }

    private async Task<ResolvedWorkflowParticipant> ResolveParticipantAsync(
        WorkflowParticipantDefinition participant,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents,
        CancellationToken cancellationToken)
    {
        return participant.Source switch
        {
            InlineAgentParticipantSource inline => ResolveInline(participant, inline.Agent),
            SavedDefinitionParticipantSource saved => await ResolveSavedAsync(
                participant,
                saved.Reference,
                savedAgents,
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Workflow participant '{participant.Id}' has no executable source.")
        };
    }

    private async Task<ResolvedWorkflowParticipant> ResolveSavedAsync(
        WorkflowParticipantDefinition participant,
        AgentDefinitionReference reference,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.Id))
        {
            throw new InvalidOperationException(
                $"Workflow participant '{participant.Id}' references a saved definition without an id.");
        }

        var catalogItem = await _definitionCatalog.FindAsync(reference, cancellationToken);
        if (catalogItem is null)
        {
            throw new InvalidOperationException(
                $"Saved {reference.Kind} '{reference.Id}' was not found for workflow participant '{participant.Id}'.");
        }

        if (reference.Kind == AgentDefinitionKind.SavedWorkflow &&
            participant.Overrides.Llm is not null)
        {
            throw new InvalidOperationException(
                $"Workflow participant '{participant.Id}' applies LLM overrides to a saved workflow.");
        }

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            var draft = ResolveSavedAgentDraft(reference, savedAgents);
            ApplyOverrides(draft, participant.Overrides);
            draft.RuntimeAgentId = participant.Id;
            draft.ShortName = participant.Id;
            return new ResolvedWorkflowParticipant
            {
                ParticipantId = participant.Id,
                DisplayName = ResolveDisplayName(participant, draft.AgentName),
                Summary = ResolveSummary(participant, draft.Summary),
                RuntimeKind = AgentRuntimeKind.LlmAgent,
                Source = new MaterializedLlmParticipantSource(draft)
            };
        }

        return new ResolvedWorkflowParticipant
        {
            ParticipantId = participant.Id,
            DisplayName = ResolveDisplayName(participant, catalogItem.Name),
            Summary = ResolveSummary(participant, catalogItem.Description),
            RuntimeKind = AgentRuntimeKind.WorkflowAgent,
            Source = new ReferencedParticipantSource(reference)
        };
    }

    private static ResolvedWorkflowParticipant ResolveInline(
        WorkflowParticipantDefinition participant,
        AgentTemplateDefinition agent)
    {
        var draft = agent.Clone();
        ApplyOverrides(draft, participant.Overrides);
        draft.RuntimeAgentId = participant.Id;
        draft.ShortName = participant.Id;

        return new ResolvedWorkflowParticipant
        {
            ParticipantId = participant.Id,
            DisplayName = ResolveDisplayName(participant, draft.AgentName),
            Summary = ResolveSummary(participant, draft.Summary),
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            Source = new MaterializedLlmParticipantSource(draft)
        };
    }

    private static AgentTemplateDefinition ResolveSavedAgentDraft(
        AgentDefinitionReference reference,
        IReadOnlyCollection<AgentTemplateDefinition> savedAgents)
    {
        if (!Guid.TryParse(reference.Id, out var agentId))
        {
            throw new InvalidOperationException(
                $"Saved agent reference '{reference.Id}' is not a valid saved-agent id.");
        }

        return savedAgents.FirstOrDefault(agent => agent.Id == agentId)?.Clone()
               ?? throw new InvalidOperationException(
                   $"Saved agent '{reference.Id}' was not found.");
    }

    private static string ResolveDisplayName(
        WorkflowParticipantDefinition participant,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(participant.Overrides.DisplayName))
        {
            return participant.Overrides.DisplayName.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? participant.Id : fallback.Trim();
    }

    private static string ResolveSummary(
        WorkflowParticipantDefinition participant,
        string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(participant.Overrides.Summary))
        {
            return participant.Overrides.Summary.Trim();
        }

        if (!string.IsNullOrWhiteSpace(participant.Summary))
        {
            return participant.Summary.Trim();
        }

        return fallback?.Trim() ?? string.Empty;
    }

    private static void ApplyOverrides(
        AgentTemplateDefinition draft,
        WorkflowParticipantOverrides overrides)
    {
        if (!string.IsNullOrWhiteSpace(overrides.DisplayName))
        {
            draft.AgentName = overrides.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Llm?.AvatarText))
        {
            draft.AvatarText = overrides.Llm.AvatarText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Llm?.Instructions))
        {
            draft.Content = overrides.Llm.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(overrides.Llm?.AppendedInstructions))
        {
            draft.Content = AppendInstructions(draft.Content, overrides.Llm.AppendedInstructions);
        }
    }

    [Obsolete]
    private static void ResolveInstructionTemplates(List<ResolvedWorkflowParticipant> participants)
    {
        var agentsById = participants
            .Where(static participant => participant.Source is MaterializedLlmParticipantSource)
            .ToDictionary(
                static participant => participant.ParticipantId,
                static participant => new WorkflowParticipantDefinition
                {
                    Id = participant.ParticipantId,
                    Role = participant.DisplayName,
                    Summary = participant.Summary,
                    Source = new InlineAgentParticipantSource(((MaterializedLlmParticipantSource)participant.Source).Agent)
                },
                StringComparer.OrdinalIgnoreCase);

        foreach (var participant in participants)
        {
            if (participant.Source is not MaterializedLlmParticipantSource materialized ||
                string.IsNullOrWhiteSpace(materialized.Agent.Content))
            {
                continue;
            }

            materialized.Agent.Content = WorkflowInstructionTemplateResolver.ResolveAgentReferences(
                materialized.Agent.Content,
                participant.ParticipantId,
                agentsById);
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

public sealed class WorkflowAgentDraftMaterializer(
    IWorkflowParticipantResolver participantResolver) : IWorkflowAgentDraftMaterializer
{
    internal WorkflowAgentDraftMaterializer(IAgentTemplateService agentDescriptionService)
        : this(new WorkflowParticipantResolver(
            agentDescriptionService,
            new AgentTemplateServiceDefinitionCatalog(agentDescriptionService)))
    {
    }

    public async Task<IOrchestrationWorkflowDefinition> MaterializeAsync(
        IOrchestrationWorkflowDefinition workflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedParticipants = await participantResolver.ResolveAsync(
            workflow,
            cancellationToken);
        var materializedParticipants = resolvedParticipants
            .Select(ToMaterializedParticipant)
            .ToList();

        return workflow switch
        {
            AgentWorkflowDefinition handoff => new AgentWorkflowDefinition
            {
                Id = handoff.Id,
                DisplayName = handoff.DisplayName,
                Description = handoff.Description,
                StartParticipantId = handoff.StartParticipantId,
                Execution = CloneExecution(handoff.Execution),
                StartInputs = CloneStartInputs(handoff.StartInputs),
                Participants = materializedParticipants,
                Handoffs = CloneHandoffs(handoff.Handoffs)
            },
            SequentialWorkflowDefinition sequential => new SequentialWorkflowDefinition
            {
                Id = sequential.Id,
                DisplayName = sequential.DisplayName,
                Description = sequential.Description,
                Execution = CloneExecution(sequential.Execution),
                StartInputs = CloneStartInputs(sequential.StartInputs),
                Participants = materializedParticipants,
                ParticipantOrder = sequential.ParticipantOrder.ToList()
            },
            ConcurrentWorkflowDefinition concurrent => new ConcurrentWorkflowDefinition
            {
                Id = concurrent.Id,
                DisplayName = concurrent.DisplayName,
                Description = concurrent.Description,
                Execution = CloneExecution(concurrent.Execution),
                StartInputs = CloneStartInputs(concurrent.StartInputs),
                Participants = materializedParticipants,
                ParticipantIds = concurrent.ParticipantIds.ToList(),
                Aggregation = CloneAggregation(concurrent.Aggregation)
            },
            GroupChatWorkflowDefinition groupChat => new GroupChatWorkflowDefinition
            {
                Id = groupChat.Id,
                DisplayName = groupChat.DisplayName,
                Description = groupChat.Description,
                Execution = CloneExecution(groupChat.Execution),
                StartInputs = CloneStartInputs(groupChat.StartInputs),
                Participants = materializedParticipants,
                ParticipantIds = groupChat.ParticipantIds.ToList(),
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
            FromParticipantId = handoff.FromParticipantId,
            ToParticipantId = handoff.ToParticipantId,
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

    private static WorkflowParticipantDefinition ToMaterializedParticipant(
        ResolvedWorkflowParticipant participant) =>
        new()
        {
            Id = participant.ParticipantId,
            Role = participant.DisplayName,
            Summary = participant.Summary,
            Source = participant.Source switch
            {
                MaterializedLlmParticipantSource llm => new InlineAgentParticipantSource(llm.Agent),
                ReferencedParticipantSource referenced => new SavedDefinitionParticipantSource(referenced.Reference),
                _ => throw new InvalidOperationException(
                    $"Resolved participant source '{participant.Source.GetType().Name}' is not supported.")
            },
        };
}

file sealed class AgentTemplateServiceDefinitionCatalog(
    IAgentTemplateService agentTemplateService) : IAgentDefinitionCatalog
{
    public async Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var agents = await agentTemplateService.GetAllAsync();
        return agents
            .Select(static agent => new AgentDefinitionDescriptor
            {
                Reference = new AgentDefinitionReference(
                    AgentDefinitionKind.SavedAgent,
                    agent.Id.ToString("D")),
                Name = agent.AgentName,
                Description = agent.Summary,
                RuntimeKind = AgentRuntimeKind.LlmAgent,
                AvatarText = agent.AvatarText,
                ModelRequirement = AgentModelRequirement.Required,
                SupportsAttachments = true
            })
            .ToList();
    }

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
        throw new KeyNotFoundException($"Saved definition '{reference.Kind}:{reference.Id}' was not found.");
}
