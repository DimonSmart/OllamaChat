using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class WorkflowDefinitionPreflightValidator(
    IAgentDefinitionDependencyGraph dependencyGraph) : IWorkflowDefinitionPreflightValidator
{
    public async Task<IReadOnlyList<AgentDefinitionLaunchProblem>> ValidateAsync(
        AgentDefinitionReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Kind != AgentDefinitionKind.SavedWorkflow)
        {
            return [];
        }

        var analysis = await dependencyGraph.AnalyzeAsync(reference, cancellationToken);
        return analysis.Problems
            .Select(static problem => new AgentDefinitionLaunchProblem(problem.Message))
            .ToList();
    }
}
