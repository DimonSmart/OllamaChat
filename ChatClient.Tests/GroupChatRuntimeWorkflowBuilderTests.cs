using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.GroupChat;
using ChatClient.Api.AgentWorkflows.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class GroupChatRuntimeWorkflowBuilderTests
{
    [Fact]
    public void CreateManager_ConfiguredRoundRobinCarriesPriorAssistantCount()
    {
        var builder = new GroupChatRuntimeWorkflowBuilder(new GroupChatManagerRegistry([]));
        var workflow = new GroupChatWorkflowDefinition
        {
            Id = "round-robin-test",
            DisplayName = "Round Robin Test",
            ParticipantAgentIds = ["host", "kant"],
            Manager = new GroupChatWorkflowManagerDefinition
            {
                Kind = GroupChatWorkflowManagerKind.RoundRobin,
                MaximumIterations = 6
            }
        };

        var manager = Assert.IsType<ConfiguredRoundRobinGroupChatManager>(
            builder.CreateManager(
                [CreateAgent("host", "Host"), CreateAgent("kant", "Kant")],
                workflow,
                new OrchestrationRuntimeBuildContext
                {
                    AssistantSpeakerIds = ["host", "kant", "host"]
                }));

        Assert.Equal(3, manager.AssistantMessagesBeforeRun);
    }

    [Fact]
    public void CreateManager_ConfiguredProgrammableManagerCarriesPriorAssistantCount()
    {
        var builder = new GroupChatRuntimeWorkflowBuilder(new GroupChatManagerRegistry([]));
        var workflow = new GroupChatWorkflowDefinition
        {
            Id = "philosopher-battle-group-chat",
            DisplayName = "Philosopher Battle Group Chat",
            ParticipantAgentIds = ["host", "debater_a", "debater_b", "judge"],
            Manager = new GroupChatWorkflowManagerDefinition
            {
                Kind = GroupChatWorkflowManagerKind.Programmable,
                MaximumIterations = 10,
                Program = GroupChatManagerPrograms.PrefixCycleSuffix(
                    prefix: ["host"],
                    cycle: ["debater_a", "debater_b"],
                    suffix: ["debater_a", "debater_b", "judge"]),
                ProgramDisplayName = "PrefixCycleSuffix"
            }
        };

        var manager = Assert.IsType<ConfiguredProgrammableGroupChatManager>(
            builder.CreateManager(
                [
                    CreateAgent("host", "Debate Host"),
                    CreateAgent("debater_a", "Immanuel Kant"),
                    CreateAgent("debater_b", "Friedrich Nietzsche"),
                    CreateAgent("judge", "Debate Judge")
                ],
                workflow,
                new OrchestrationRuntimeBuildContext
                {
                    AssistantSpeakerIds = ["host", "debater_a", "debater_b", "debater_a", "debater_b", "debater_a", "debater_b", "debater_a", "judge"]
                }));

        Assert.Equal(9, manager.AssistantMessagesBeforeRun);
    }

    private static AIAgent CreateAgent(string id, string name)
    {
        return new StubAgent(id, name);
    }

    private sealed class StubAgent(string id, string name) : AIAgent
    {
        public override string Name => name;

        public override string? Description => null;

        protected override string IdCore => id;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionData,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
