using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.Client.Components.Planning;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Tests;

public class PlanningGraphLinkProjectionTests
{
    [Fact]
    public void Build_ReturnsDependencyAndResultDescriptors()
    {
        var plan = CreatePlan();
        var finalResult = ResultEnvelope<JsonElement?>.Success(
            JsonSerializer.SerializeToElement("done"));

        var descriptors = PlanningGraphLinkProjection.Build(plan.Steps, finalResult);

        var dependency = Assert.Single(descriptors, descriptor => descriptor.Kind == PlanningGraphLinkKind.Dependency);
        Assert.Equal("step1", dependency.SourceId);
        Assert.Equal("step2", dependency.TargetId);
        Assert.Equal(
            PlanningGraphLinkDescriptor.CreateId("step1", "step2", PlanningGraphLinkKind.Dependency),
            dependency.Id);

        var match = Assert.Single(dependency.Matches);
        Assert.Equal("searchResults", match.InputName);
        Assert.Equal("searchResults", match.Path);
        Assert.Equal("$step1", match.Reference);
        Assert.Equal("map", match.Mode);

        var resultLink = Assert.Single(descriptors, descriptor => descriptor.Kind == PlanningGraphLinkKind.Result);
        Assert.Equal("step2", resultLink.SourceId);
        Assert.Equal(PlanningVirtualNodeDescriptor.ResultNodeId, resultLink.TargetId);
    }

    [Fact]
    public void BuildSelectionKeys_IncludesLinkIds()
    {
        var plan = CreatePlan();
        var finalResult = ResultEnvelope<JsonElement?>.Success(
            JsonSerializer.SerializeToElement("done"));

        var selectionKeys = PlanningVirtualNodeProjection.BuildSelectionKeys(
            plan,
            [],
            [],
            finalResult);

        Assert.Contains(
            PlanningGraphLinkDescriptor.CreateId("step1", "step2", PlanningGraphLinkKind.Dependency),
            selectionKeys);
        Assert.Contains(
            PlanningGraphLinkDescriptor.CreateId("step2", PlanningVirtualNodeDescriptor.ResultNodeId, PlanningGraphLinkKind.Result),
            selectionKeys);
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
                    In = new Dictionary<string, JsonNode?>(),
                    Status = PlanStepStatuses.Done
                },
                new PlanStep
                {
                    Id = "step2",
                    Llm = "summarize",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["searchResults"] = new JsonObject
                        {
                            ["from"] = "$step1",
                            ["mode"] = "map"
                        }
                    },
                    Status = PlanStepStatuses.Done
                }
            ]
        };
}
