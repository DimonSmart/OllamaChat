using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public interface IOrchestrationRuntimeWorkflowBuilder
{
    bool CanBuild(IOrchestrationWorkflowDefinition workflow);

    Workflow Build(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> agentsById,
        OrchestrationRuntimeBuildContext context);
}
