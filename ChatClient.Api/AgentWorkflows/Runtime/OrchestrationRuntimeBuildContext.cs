namespace ChatClient.Api.AgentWorkflows.Runtime;

public sealed class OrchestrationRuntimeBuildContext
{
    public IReadOnlyList<string> AssistantSpeakerIds { get; init; } = [];
}
