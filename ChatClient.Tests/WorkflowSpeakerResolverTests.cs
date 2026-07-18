using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;

namespace ChatClient.Tests;

public sealed class WorkflowSpeakerResolverTests
{
    [Fact]
    public void ResolveFromExecutorId_ReturnsMappedWorkflowAgentId_WhenExecutorIdMatchesRuntimeExecutor()
    {
        var speakerId = WorkflowSpeakerResolver.ResolveFromExecutorId(
            "runtime://agents/reviewer-b",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["runtime://agents/reviewer-b"] = "reviewer_b"
            });

        Assert.Equal("reviewer_b", speakerId);
    }

    [Fact]
    public void ResolveFromExecutorId_ReturnsMappedWorkflowAgentId_WhenExecutorIdMatchesAgentName()
    {
        var speakerId = WorkflowSpeakerResolver.ResolveFromExecutorId(
            "Review Closer",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Review Closer"] = "closer"
            });

        Assert.Equal("closer", speakerId);
    }

    [Fact]
    [Obsolete]
    public void ResolveFromWorkflow_UsesProgrammableGroupChatSchedule()
    {
        var workflow = CreateProgrammableReviewWorkflow();

        Assert.Equal("host", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 0));
        Assert.Equal("reviewer_a", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 1));
        Assert.Equal("reviewer_b", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 2));
        Assert.Equal("reviewer_a", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 7));
        Assert.Equal("reviewer_b", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 8));
        Assert.Equal("closer", WorkflowSpeakerResolver.ResolveFromWorkflow(workflow, 9));
    }

    [Fact]
    [Obsolete]
    public void ResolveSpeakerId_FallsBackToWorkflowSchedule_WhenExecutorIdIsMissing()
    {
        var workflow = CreateProgrammableReviewWorkflow();

        var speakerId = WorkflowSpeakerResolver.ResolveSpeakerId(
            executorId: null,
            agentIdsByExecutorId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            workflow: workflow,
            assistantMessageIndex: 1);

        Assert.Equal("reviewer_a", speakerId);
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

    [Obsolete]
    private static GroupChatWorkflowDefinition CreateProgrammableReviewWorkflow()
    {
        return new GroupChatWorkflowDefinition
        {
            Id = "structured-review-group-chat",
            DisplayName = "Structured Review Group Chat",
            Agents = [],
            ParticipantAgentIds = ["host", "reviewer_a", "reviewer_b", "closer"],
            Manager = new GroupChatWorkflowManagerDefinition
            {
                Kind = GroupChatWorkflowManagerKind.Programmable,
                MaximumIterations = 10,
                Program = GroupChatManagerPrograms.PrefixCycleSuffix(
                    prefix: ["host"],
                    cycle: ["reviewer_a", "reviewer_b"],
                    suffix: ["reviewer_a", "reviewer_b", "closer"]),
                ProgramDisplayName = "PrefixCycleSuffix"
            }
        };
    }
}
