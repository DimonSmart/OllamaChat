using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using Microsoft.Extensions.Options;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class AgentDefinitionDependencyGraph(
    IAgentTemplateService agentTemplateService,
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    ILegacyWorkflowDefinitionNormalizer legacyWorkflowDefinitionNormalizer,
    IWorkflowParticipantResolver workflowParticipantResolver,
    IOptions<AgentRuntimeOptions> options) : IAgentDefinitionDependencyGraph
{
    public async Task<AgentDefinitionDependencyAnalysis> AnalyzeAsync(
        AgentDefinitionReference root,
        CancellationToken cancellationToken = default)
    {
        var state = new AnalysisState();
        await VisitAsync(
            root,
            [],
            null,
            null,
            state,
            cancellationToken);

        return new AgentDefinitionDependencyAnalysis
        {
            Root = root,
            Nodes = state.Nodes.Values.ToList(),
            Edges = state.Edges,
            Problems = state.Problems
        };
    }

    private async Task VisitAsync(
        AgentDefinitionReference reference,
        IReadOnlyList<AgentDefinitionTraversalFrame> path,
        string? parentParticipantId,
        string? parentParticipantDisplayName,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (path.Any(frame => AgentDefinitionReferenceComparer.Instance.Equals(frame.Definition, reference)))
        {
            var cyclePath = path.Concat([
                CreateFrame(
                    reference,
                    state.GetDisplayName(reference),
                    parentParticipantId,
                    parentParticipantDisplayName)
            ]).ToList();
            state.Problems.Add(new AgentDefinitionProblem(
                "Workflow dependency cycle detected:" + Environment.NewLine + FormatPath(cyclePath)));
            return;
        }

        var resolution = await ResolveAsync(reference, state, cancellationToken);
        state.SetDisplayName(reference, resolution.DisplayName);

        var nextPath = path.Concat([
            CreateFrame(
                reference,
                resolution.DisplayName,
                parentParticipantId,
                parentParticipantDisplayName)
        ]).ToList();

        var workflowDepth = nextPath.Count(static frame =>
            frame.Definition.Kind == AgentDefinitionKind.SavedWorkflow);
        if (reference.Kind == AgentDefinitionKind.SavedWorkflow &&
            workflowDepth > options.Value.MaximumWorkflowNestingDepth)
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Workflow nesting limit exceeded. Maximum depth is {options.Value.MaximumWorkflowNestingDepth}." +
                Environment.NewLine +
                FormatPath(nextPath)));
            return;
        }

        if (resolution.Node is { } node)
        {
            state.Nodes.TryAdd(Key(reference), node);
        }

        switch (resolution.Status)
        {
            case DefinitionResolutionStatus.Resolved:
                break;
            case DefinitionResolutionStatus.Missing:
                state.Problems.Add(new AgentDefinitionProblem(
                    FormatMissing(reference, resolution.Message, nextPath)));
                return;
            case DefinitionResolutionStatus.Invalid:
                state.Problems.Add(new AgentDefinitionProblem(
                    FormatInvalid(reference, resolution.Message, nextPath)));
                return;
            case DefinitionResolutionStatus.Unsupported:
                state.Problems.Add(new AgentDefinitionProblem(
                    FormatUnsupported(reference, resolution.Message, nextPath)));
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported dependency resolution status '{resolution.Status}'.");
        }

        if (!state.Traversed.Add(Key(reference)) ||
            resolution.Workflow is null)
        {
            return;
        }

        foreach (var participant in resolution.Workflow.Participants)
        {
            if (participant.Source is not ReferencedParticipantSource referenced)
            {
                continue;
            }

            state.Edges.Add(new AgentDefinitionDependencyEdge
            {
                Parent = reference,
                Child = referenced.Reference,
                ParticipantId = participant.ParticipantId,
                ParticipantDisplayName = participant.DisplayName
            });

            await VisitAsync(
                referenced.Reference,
                nextPath,
                participant.ParticipantId,
                participant.DisplayName,
                state,
                cancellationToken);
        }
    }

    private async Task<DefinitionResolutionResult> ResolveAsync(
        AgentDefinitionReference reference,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        var cacheKey = Key(reference);
        if (state.ResolutionCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        DefinitionResolutionResult result = reference.Kind switch
        {
            AgentDefinitionKind.SavedAgent => await ResolveSavedAgentAsync(reference),
            AgentDefinitionKind.SavedWorkflow => await ResolveWorkflowAsync(reference, cancellationToken),
            _ => DefinitionResolutionResult.Unsupported(
                reference.Id,
                $"Definition reference '{reference.Kind}:{reference.Id}' has unsupported kind.")
        };

        state.ResolutionCache.Add(cacheKey, result);
        return result;
    }

    private async Task<DefinitionResolutionResult> ResolveSavedAgentAsync(
        AgentDefinitionReference reference)
    {
        if (!Guid.TryParse(reference.Id, out var agentId))
        {
            return DefinitionResolutionResult.Invalid(
                reference.Id,
                $"Saved agent reference '{reference.Id}' is not a valid saved-agent id.");
        }

        var agent = await agentTemplateService.GetByIdAsync(agentId);
        if (agent is null)
        {
            return DefinitionResolutionResult.Missing(
                reference.Id,
                $"references missing saved agent {reference.Id}");
        }

        return DefinitionResolutionResult.Resolved(
            agent.AgentName,
            new AgentDefinitionDependencyNode
            {
                Definition = reference,
                DisplayName = agent.AgentName,
                RuntimeKind = AgentRuntimeKind.LlmAgent
            },
            null);
    }

    private async Task<DefinitionResolutionResult> ResolveWorkflowAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            return DefinitionResolutionResult.Invalid(
                reference.Id,
                $"Workflow id '{reference.Id}' is not a valid saved-workflow id.");
        }

        var savedWorkflow = await workflowDefinitionService.GetByIdAsync(workflowId);
        if (savedWorkflow is null)
        {
            return DefinitionResolutionResult.Missing(
                reference.Id,
                $"references missing saved workflow {reference.Id}");
        }

        try
        {
            var compiled = await workflowDefinitionCompiler.CompileAsync(
                savedWorkflow.SourceCode,
                cancellationToken);
            if (compiled.Workflow is null)
            {
                return DefinitionResolutionResult.Invalid(
                    savedWorkflow.DisplayName,
                    $"Workflow '{savedWorkflow.DisplayName}' compilation did not return a workflow definition.");
            }

            var workflow = await legacyWorkflowDefinitionNormalizer.NormalizeAsync(
                compiled.Workflow,
                cancellationToken);
            var participants = await workflowParticipantResolver.ResolveAsync(
                workflow,
                cancellationToken);

            return DefinitionResolutionResult.Resolved(
                savedWorkflow.DisplayName,
                new AgentDefinitionDependencyNode
                {
                    Definition = reference,
                    DisplayName = savedWorkflow.DisplayName,
                    RuntimeKind = AgentRuntimeKind.WorkflowAgent
                },
                new ResolvedWorkflow(workflow, participants));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DefinitionResolutionResult.Invalid(
                savedWorkflow.DisplayName,
                $"Workflow '{savedWorkflow.DisplayName}' is invalid: {ex.Message}");
        }
    }

    private static AgentDefinitionTraversalFrame CreateFrame(
        AgentDefinitionReference definition,
        string displayName,
        string? parentParticipantId,
        string? parentParticipantDisplayName) =>
        new()
        {
            Definition = definition,
            DisplayName = displayName,
            ParentParticipantId = parentParticipantId,
            ParentParticipantDisplayName = parentParticipantDisplayName
        };

    private static string FormatMissing(
        AgentDefinitionReference reference,
        string message,
        IReadOnlyList<AgentDefinitionTraversalFrame> path) =>
        FormatPath(path) + Environment.NewLine + message;

    private static string FormatInvalid(
        AgentDefinitionReference reference,
        string message,
        IReadOnlyList<AgentDefinitionTraversalFrame> path) =>
        FormatPath(path) + Environment.NewLine + message;

    private static string FormatUnsupported(
        AgentDefinitionReference reference,
        string message,
        IReadOnlyList<AgentDefinitionTraversalFrame> path) =>
        FormatPath(path) + Environment.NewLine + message;

    private static string FormatPath(
        IReadOnlyList<AgentDefinitionTraversalFrame> path)
    {
        var lines = new List<string>();
        foreach (var frame in path)
        {
            if (!string.IsNullOrWhiteSpace(frame.ParentParticipantDisplayName) ||
                !string.IsNullOrWhiteSpace(frame.ParentParticipantId))
            {
                var participant = string.IsNullOrWhiteSpace(frame.ParentParticipantDisplayName)
                    ? frame.ParentParticipantId
                    : frame.ParentParticipantDisplayName;
                lines.Add($"  -> participant \"{participant}\"");
            }

            lines.Add(frame.DisplayName);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Key(AgentDefinitionReference reference) =>
        AgentDefinitionReferenceComparer.Instance.GetKey(reference);

    private sealed record ResolvedWorkflow(
        IOrchestrationWorkflowDefinition Definition,
        IReadOnlyList<ResolvedWorkflowParticipant> Participants);

    private enum DefinitionResolutionStatus
    {
        Resolved,
        Missing,
        Invalid,
        Unsupported
    }

    private sealed record DefinitionResolutionResult
    {
        public required DefinitionResolutionStatus Status { get; init; }

        public required string DisplayName { get; init; }

        public string Message { get; init; } = string.Empty;

        public AgentDefinitionDependencyNode? Node { get; init; }

        public ResolvedWorkflow? Workflow { get; init; }

        public static DefinitionResolutionResult Resolved(
            string displayName,
            AgentDefinitionDependencyNode node,
            ResolvedWorkflow? workflow) =>
            new()
            {
                Status = DefinitionResolutionStatus.Resolved,
                DisplayName = displayName,
                Node = node,
                Workflow = workflow
            };

        public static DefinitionResolutionResult Missing(
            string displayName,
            string message) =>
            new()
            {
                Status = DefinitionResolutionStatus.Missing,
                DisplayName = displayName,
                Message = message
            };

        public static DefinitionResolutionResult Invalid(
            string displayName,
            string message) =>
            new()
            {
                Status = DefinitionResolutionStatus.Invalid,
                DisplayName = displayName,
                Message = message
            };

        public static DefinitionResolutionResult Unsupported(
            string displayName,
            string message) =>
            new()
            {
                Status = DefinitionResolutionStatus.Unsupported,
                DisplayName = displayName,
                Message = message
            };
    }

    private sealed class AnalysisState
    {
        public Dictionary<string, AgentDefinitionDependencyNode> Nodes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<AgentDefinitionDependencyEdge> Edges { get; } = [];

        public List<AgentDefinitionProblem> Problems { get; } = [];

        public Dictionary<string, DefinitionResolutionResult> ResolutionCache { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Traversed { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> DisplayNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string GetDisplayName(AgentDefinitionReference reference) =>
            DisplayNames.TryGetValue(Key(reference), out var displayName)
                ? displayName
                : reference.Id;

        public void SetDisplayName(
            AgentDefinitionReference reference,
            string displayName)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                DisplayNames[Key(reference)] = displayName;
            }
        }
    }
}
