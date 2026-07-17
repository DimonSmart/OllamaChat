using ChatClient.Api.AgentWorkflows.GroupChat;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class GroupChatRuntimeWorkflowBuilder(
    GroupChatManagerRegistry managerRegistry) : IOrchestrationRuntimeWorkflowBuilder
{
    private readonly GroupChatManagerRegistry _managerRegistry = managerRegistry;

    public bool CanBuild(IOrchestrationWorkflowDefinition workflow) =>
        workflow is GroupChatWorkflowDefinition;

    public Workflow Build(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> agentsById,
        OrchestrationRuntimeBuildContext context)
    {
        var groupChatWorkflow = workflow as GroupChatWorkflowDefinition
                                ?? throw new InvalidOperationException(
                                    $"Workflow kind '{workflow.Kind}' is not supported by {nameof(GroupChatRuntimeWorkflowBuilder)}.");

        var participantAgents = groupChatWorkflow.ParticipantIds
            .Select(agentId => agentsById.TryGetValue(agentId, out var agent)
                ? agent
                : throw new InvalidOperationException(
                    $"Workflow participant '{agentId}' was not prepared."))
            .ToList();

        var builder = AgentWorkflowBuilder.CreateGroupChatBuilderWith(
                agents => CreateManager(agents, groupChatWorkflow, context))
            .WithName(groupChatWorkflow.DisplayName)
            .WithDescription(groupChatWorkflow.Description)
            .AddParticipants(participantAgents);

        return builder.Build();
    }

    internal GroupChatManager CreateManager(
        IReadOnlyList<AIAgent> agents,
        GroupChatWorkflowDefinition workflow,
        OrchestrationRuntimeBuildContext context) =>
        workflow.Manager.Kind switch
        {
            GroupChatWorkflowManagerKind.RoundRobin => new ConfiguredRoundRobinGroupChatManager(
                agents,
                workflow.Manager.MaximumIterations,
                context.AssistantSpeakerIds.Count),
            GroupChatWorkflowManagerKind.Programmable => new ConfiguredProgrammableGroupChatManager(
                agents,
                workflow.ParticipantIds,
                workflow.Manager,
                context.AssistantSpeakerIds),
            GroupChatWorkflowManagerKind.Custom => _managerRegistry.Create(
                workflow.Manager.ImplementationKey
                ?? throw new InvalidOperationException("Custom group chat managers require an implementation key."),
                agents,
                workflow,
                context.AssistantSpeakerIds),
            _ => throw new InvalidOperationException(
                $"Unsupported group chat manager kind '{workflow.Manager.Kind}'.")
        };
}
