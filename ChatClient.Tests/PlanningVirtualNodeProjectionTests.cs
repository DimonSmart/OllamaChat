using System.Text.Json;
using ChatClient.Api.Client.Components.Planning;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;

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

        var resultNode = Assert.Single(nodes.Where(node => node.Kind == PlanningVirtualNodeKind.Result));
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

    private static PlanDefinition CreatePlan() =>
        new()
        {
            Goal = "Return a final answer.",
            Steps =
            [
                new PlanStep
                {
                    Id = "step1",
                    Tool = "search",
                    In = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(),
                    Status = PlanStepStatuses.Done
                }
            ]
        };
}
