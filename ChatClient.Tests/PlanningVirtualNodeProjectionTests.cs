using System.Text.Json;
using ChatClient.Api.Client.Components.Planning;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Verification;

namespace ChatClient.Tests;

public class PlanningVirtualNodeProjectionTests
{
    [Fact]
    public void Build_IncludesResultNode_WhenFinalResultExists()
    {
        var plan = CreatePlan();
        var finalResult = ResultEnvelope<JsonElement?>.Success(
            JsonSerializer.SerializeToElement("# Answer\n\n- first\n- second"));

        var nodes = PlanningVirtualNodeProjection.Build(
            plan,
            [],
            [],
            finalResult);

        var resultNode = Assert.Single(nodes, node => node.Kind == PlanningVirtualNodeKind.Result);
        Assert.Equal(PlanningVirtualNodeDescriptor.ResultNodeId, resultNode.Id);
        Assert.Equal(PlanStepStatuses.Done, resultNode.StatusValue);
        Assert.Equal("ok: true", resultNode.Subtitle);
        Assert.NotNull(resultNode.FinalResult);
    }

    [Fact]
    public void ResolveDefaultSelectionId_PrefersResultNode_WhenRunIsFinished()
    {
        var plan = CreatePlan();
        var finalResult = ResultEnvelope<JsonElement?>.Success(
            JsonSerializer.SerializeToElement("Final answer"));

        var selectedNodeId = PlanningVirtualNodeProjection.ResolveDefaultSelectionId(
            plan,
            [],
            [],
            finalResult,
            activeStepId: null);

        Assert.Equal(PlanningVirtualNodeDescriptor.ResultNodeId, selectedNodeId);
    }

    [Fact]
    public void Build_MarksLastReplanNodeAsFail_WhenRunFinishedWithError()
    {
        var plan = CreatePlan();
        var finalResult = ResultEnvelope<JsonElement?>.Failure("planning_failed", "Replanner could not produce a valid plan.");
        var events = new PlanRunEvent[]
        {
            new ReplanStartedEvent(new PlannerReplanRequest
            {
                UserQuery = "Find DIY robots.",
                AttemptNumber = 1,
                Plan = plan,
                ExecutionResult = new ExecutionResult(),
                GoalVerdict = new GoalVerdict
                {
                    Action = GoalAction.Replan,
                    Reason = "Execution has failed steps."
                }
            }),
            new ReplanRoundCompletedEvent(
                Round: 1,
                Done: false,
                Reason: "Replanner could not produce a valid plan after 10 rounds.",
                ActionBatch: JsonSerializer.SerializeToElement(new { completion = "Replan repaired." }),
                ActionResults: JsonSerializer.SerializeToElement(Array.Empty<object>()))
        };

        var nodes = PlanningVirtualNodeProjection.Build(
            plan,
            events,
            [],
            finalResult);

        var replanNode = Assert.Single(nodes, node => node.Kind == PlanningVirtualNodeKind.Replanning);
        Assert.Equal(PlanStepStatuses.Fail, replanNode.StatusValue);
    }

    private static PlanDefinition CreatePlan() =>
        new()
        {
            Goal = "Return a final answer.",
            Steps =
            [
                new PlanStep
                {
                    Id = "step1",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "search",
                    In = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(),
                    Status = PlanStepStatuses.Done
                }
            ]
        };
}




