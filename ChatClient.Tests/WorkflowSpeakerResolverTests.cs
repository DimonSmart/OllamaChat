using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;

namespace ChatClient.Tests;

public sealed class WorkflowSpeakerResolverTests
{
    [Fact]
    public void ResolveFromExecutorId_ReturnsMappedWorkflowAgentId_WhenExecutorIdMatchesRuntimeExecutor()
    {
        var speakerId = WorkflowSpeakerResolver.ResolveFromExecutorId(
            "runtime://agents/nietzsche",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["runtime://agents/nietzsche"] = "nietzsche"
            });

        Assert.Equal("nietzsche", speakerId);
    }

    [Fact]
    public void ResolveFromExecutorId_ReturnsMappedWorkflowAgentId_WhenExecutorIdMatchesAgentName()
    {
        var speakerId = WorkflowSpeakerResolver.ResolveFromExecutorId(
            "Debate Judge",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Debate Judge"] = "judge"
            });

        Assert.Equal("judge", speakerId);
    }

    [Fact]
    public void ResolveFromWorkflow_UsesProgrammableGroupChatSchedule()
    {
        var workflow = CreateProgrammableDebateWorkflow();

        Assert.Equal("host", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 0));
        Assert.Equal("debater_a", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 1));
        Assert.Equal("debater_b", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 2));
        Assert.Equal("debater_a", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 7));
        Assert.Equal("debater_b", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 8));
        Assert.Equal("judge", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 9));
    }

    [Fact]
    public void ResolveSpeakerId_FallsBackToWorkflowSchedule_WhenExecutorIdIsMissing()
    {
        var workflow = CreateProgrammableDebateWorkflow();

        var speakerId = WorkflowSpeakerResolver.ResolveSpeakerId(
            executorId: null,
            agentIdsByExecutorId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            workflow: workflow,
            assistantMessageIndex: 1);

        Assert.Equal("debater_a", speakerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-executor")]
    public void ResolveFromExecutorId_ReturnsNull_WhenExecutorIsMissingOrUnknown(string? executorId)
    {
        var speakerId = WorkflowSpeakerResolver.ResolveFromExecutorId(
            executorId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["runtime://agents/host"] = "host"
            });

        Assert.Null(speakerId);
    }

    private static GroupChatWorkflowDefinition CreateProgrammableDebateWorkflow()
    {
        return new GroupChatWorkflowDefinition
        {
            Id = "philosopher-battle-group-chat",
            DisplayName = "Philosopher Battle Group Chat",
            Agents = [],
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
    }
}
