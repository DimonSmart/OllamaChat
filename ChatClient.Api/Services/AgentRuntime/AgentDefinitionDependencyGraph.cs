using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
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
        var state = new AnalysisState(root);
        await VisitAsync(root, [], 0, state, cancellationToken);

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
        IReadOnlyList<AgentDefinitionReference> stack,
        int workflowDepth,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (stack.Any(item => SameReference(item, reference)))
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Workflow dependency cycle detected: {FormatPath(stack.Concat([reference]))}."));
            return;
        }

        if (reference.Kind == AgentDefinitionKind.SavedWorkflow &&
            workflowDepth + 1 > options.Value.MaximumWorkflowNestingDepth)
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Workflow nesting limit exceeded at {reference.Id}. Maximum depth is {options.Value.MaximumWorkflowNestingDepth}."));
            return;
        }

        var cacheKey = Key(reference);
        if (state.Visited.Contains(cacheKey))
        {
            return;
        }

        if (reference.Kind == AgentDefinitionKind.SavedAgent)
        {
            await VisitSavedAgentAsync(reference, state);
            return;
        }

        if (reference.Kind != AgentDefinitionKind.SavedWorkflow)
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Definition reference '{reference.Kind}:{reference.Id}' has unsupported kind."));
            return;
        }

        var resolved = await ResolveWorkflowAsync(reference, state, cancellationToken);
        if (resolved is null)
        {
            return;
        }

        state.Visited.Add(cacheKey);
        var nextStack = stack.Concat([reference]).ToList();
        foreach (var participant in resolved.Participants)
        {
            if (participant.Source is not ReferencedParticipantSource referenced)
            {
                continue;
            }

            state.Edges.Add(new AgentDefinitionDependencyEdge
            {
                Parent = reference,
                Child = referenced.Reference,
                ParticipantId = participant.ParticipantId
            });

            await VisitAsync(
                referenced.Reference,
                nextStack,
                reference.Kind == AgentDefinitionKind.SavedWorkflow ? workflowDepth + 1 : workflowDepth,
                state,
                cancellationToken);
        }
    }

    private async Task VisitSavedAgentAsync(
        AgentDefinitionReference reference,
        AnalysisState state)
    {
        if (!Guid.TryParse(reference.Id, out var agentId))
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Saved agent reference '{reference.Id}' is not a valid saved-agent id."));
            return;
        }

        var agent = await agentTemplateService.GetByIdAsync(agentId);
        if (agent is null)
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Saved agent '{reference.Id}' was not found."));
            return;
        }

        state.Nodes.TryAdd(Key(reference), new AgentDefinitionDependencyNode
        {
            Definition = reference,
            DisplayName = agent.AgentName,
            RuntimeKind = AgentRuntimeKind.LlmAgent
        });
        state.Visited.Add(Key(reference));
    }

    private async Task<ResolvedWorkflow?> ResolveWorkflowAsync(
        AgentDefinitionReference reference,
        AnalysisState state,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference.Id, out var workflowId))
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Workflow id '{reference.Id}' is not a valid saved-workflow id."));
            return null;
        }

        var savedWorkflow = await workflowDefinitionService.GetByIdAsync(workflowId);
        if (savedWorkflow is null)
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Saved workflow '{reference.Id}' was not found."));
            return null;
        }

        try
        {
            var compiled = await workflowDefinitionCompiler.CompileAsync(
                savedWorkflow.SourceCode,
                cancellationToken);
            if (compiled.Workflow is null)
            {
                state.Problems.Add(new AgentDefinitionProblem(
                    $"Workflow '{savedWorkflow.DisplayName}' compilation did not return a workflow definition."));
                return null;
            }

            var workflow = await legacyWorkflowDefinitionNormalizer.NormalizeAsync(
                compiled.Workflow,
                cancellationToken);
            var participants = await workflowParticipantResolver.ResolveAsync(
                workflow,
                cancellationToken);

            state.Nodes.TryAdd(Key(reference), new AgentDefinitionDependencyNode
            {
                Definition = reference,
                DisplayName = savedWorkflow.DisplayName,
                RuntimeKind = AgentRuntimeKind.WorkflowAgent
            });

            return new ResolvedWorkflow(workflow, participants);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Problems.Add(new AgentDefinitionProblem(
                $"Workflow '{savedWorkflow.DisplayName}' is invalid: {ex.Message}"));
            return null;
        }
    }

    private static string FormatPath(IEnumerable<AgentDefinitionReference> path) =>
        string.Join(" -> ", path.Select(static reference => $"{reference.Kind}:{reference.Id}"));

    private static bool SameReference(
        AgentDefinitionReference left,
        AgentDefinitionReference right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);

    private static string Key(AgentDefinitionReference reference) =>
        $"{reference.Kind}:{reference.Id}";

    private sealed record ResolvedWorkflow(
        IOrchestrationWorkflowDefinition Workflow,
        IReadOnlyList<ResolvedWorkflowParticipant> Participants);

    private sealed class AnalysisState(AgentDefinitionReference root)
    {
        public AgentDefinitionReference Root { get; } = root;

        public Dictionary<string, AgentDefinitionDependencyNode> Nodes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<AgentDefinitionDependencyEdge> Edges { get; } = [];

        public List<AgentDefinitionProblem> Problems { get; } = [];

        public HashSet<string> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
