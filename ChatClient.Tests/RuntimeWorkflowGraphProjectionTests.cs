using ChatClient.Api.Client.Components.Planning;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Runtime;
using System.Text.Json;

namespace ChatClient.Tests;

public class RuntimeWorkflowGraphProjectionTests
{
    [Fact]
    public void Build_IncludesStageArtifactNodesAndEntryLinks()
    {
        var brief = CreateRequestBrief();
        var outlinePlan = CreateOutlinePlan();
        var lowLevelPlan = CreateLowLevelPlan();
        var runtimePlan = CreateRuntimePlan();
        var events = new PlanRunEvent[]
        {
            new PlanningAttemptStartedEvent(1, "three_layer", "Find lyrics and translate them."),
            new RequestAnalysisCompletedEvent(brief),
            new OutlineStageCompletedEvent(outlinePlan, "{}", [], true),
            new LowLevelStageCompletedEvent(lowLevelPlan, "{}", [], true),
            new RuntimeCompilationCompletedEvent(runtimePlan, [], true)
        };

        var graph = RuntimeWorkflowGraphProjection.Build(
            runtimePlan,
            events,
            finalResult: null,
            activeRuntimeStepId: null);

        Assert.Contains(graph.Nodes, node => node.Kind == RuntimeWorkflowNodeKind.RequestBrief);
        Assert.Contains(graph.Nodes, node => node.Kind == RuntimeWorkflowNodeKind.OutlinePlan);
        Assert.Contains(graph.Nodes, node => node.Kind == RuntimeWorkflowNodeKind.LowLevelPlan);
        Assert.Contains(graph.Nodes, node => node.Kind == RuntimeWorkflowNodeKind.RuntimePlan);
        Assert.Contains(graph.Nodes, node => node.Kind == RuntimeWorkflowNodeKind.Step);

        Assert.Contains(
            graph.Links,
            link => link.Kind == RuntimeWorkflowLinkKind.Stage
                && link.SourceId == RuntimeWorkflowGraphProjection.RequestBriefNodeId
                && link.TargetId == RuntimeWorkflowGraphProjection.OutlinePlanNodeId);
        Assert.Contains(
            graph.Links,
            link => link.Kind == RuntimeWorkflowLinkKind.Stage
                && link.SourceId == RuntimeWorkflowGraphProjection.OutlinePlanNodeId
                && link.TargetId == RuntimeWorkflowGraphProjection.LowLevelPlanNodeId);
        Assert.Contains(
            graph.Links,
            link => link.Kind == RuntimeWorkflowLinkKind.Stage
                && link.SourceId == RuntimeWorkflowGraphProjection.LowLevelPlanNodeId
                && link.TargetId == RuntimeWorkflowGraphProjection.RuntimePlanNodeId);
        Assert.Contains(
            graph.Links,
            link => link.Kind == RuntimeWorkflowLinkKind.Stage
                && link.SourceId == RuntimeWorkflowGraphProjection.RuntimePlanNodeId
                && link.TargetId == "step1");
    }

    [Fact]
    public void Build_ReturnsArtifactGraph_WhenOnlyRequestBriefExists()
    {
        var brief = CreateRequestBrief();
        var events = new PlanRunEvent[]
        {
            new PlanningAttemptStartedEvent(1, "three_layer", "Find lyrics and translate them."),
            new RequestAnalysisCompletedEvent(brief)
        };

        var graph = RuntimeWorkflowGraphProjection.Build(
            null,
            events,
            finalResult: null,
            activeRuntimeStepId: null);

        Assert.True(graph.HasContent);
        Assert.Contains(
            graph.Nodes,
            node => node.Kind == RuntimeWorkflowNodeKind.RequestBrief && node.RequestBrief is not null);
        Assert.Contains(
            graph.Nodes,
            node => node.Kind == RuntimeWorkflowNodeKind.OutlinePlan && node.Status == "running");
        Assert.Equal(RuntimeWorkflowGraphProjection.RequestBriefNodeId, graph.DefaultSelectionId);
    }

    [Fact]
    public void Build_AddsDependencyAndResultLinks_WhenRuntimePlanAndFinalResultExist()
    {
        var runtimePlan = CreateRuntimePlan();
        var finalResult = ResultEnvelope<JsonElement?>.Success(
            JsonSerializer.SerializeToElement(new { summary = "Collected the lyrics." }));
        var events = new PlanRunEvent[]
        {
            new RuntimeStepStartedEvent(
                "step1",
                "tool",
                JsonSerializer.SerializeToElement(new { query = "lyrics" })),
            new RuntimeStepCompletedEvent(
                "step1",
                true,
                JsonSerializer.SerializeToElement(new { results = new[] { "doc1" } }),
                null)
        };

        var graph = RuntimeWorkflowGraphProjection.Build(
            runtimePlan,
            events,
            finalResult,
            activeRuntimeStepId: "step2");

        Assert.Contains(
            graph.Links,
            link => link.Kind == RuntimeWorkflowLinkKind.Dependency
                && link.SourceId == "step1"
                && link.TargetId == "step2");
        Assert.Contains(
            graph.Links,
            link => link.Kind == RuntimeWorkflowLinkKind.Result
                && link.SourceId == "step2"
                && link.TargetId == RuntimeWorkflowGraphProjection.ResultNodeId);

        var activeNode = Assert.Single(graph.Nodes, node => node.Id == "step2");
        Assert.True(activeNode.IsActive);
        Assert.Equal("running", activeNode.Status);
    }

    private static RequestBrief CreateRequestBrief() =>
        new()
        {
            RewrittenRequest = "Find the lyrics and present them with an English translation.",
            Goal = "Return the requested lyrics in a translated table.",
            ExpectedResult = "translation table",
            Deliverables = ["original lyrics", "English translation"],
            Constraints = ["Use only the requested song."],
            AcquisitionNeeds = ["candidate lyrics pages"],
            EvidenceRequirements = ["lyrics text"],
            ReasoningNeeds = ["pair original and translated lines"],
            SuccessCriteria = ["All lines are present."],
            AmbiguityNotes = [],
            OutputExpectations = "markdown table",
            SuggestedPlanOutline = ["find candidate pages", "extract lyrics", "translate lines"]
        };

    private static OutlinePlan CreateOutlinePlan() =>
        new()
        {
            Goal = "Find the lyrics and translate them.",
            ResultNodeId = "outline-result",
            Nodes =
            [
                new OutlineNode
                {
                    Id = "discover-lyrics",
                    Kind = OutlineNodeKinds.Discover,
                    Purpose = "Find candidate lyrics pages."
                },
                new OutlineNode
                {
                    Id = "outline-result",
                    Kind = OutlineNodeKinds.Answer,
                    Purpose = "Return the translated lyrics.",
                    DependsOn = ["discover-lyrics"]
                }
            ]
        };

    private static LowLevelPlan CreateLowLevelPlan() =>
        new()
        {
            Goal = "Find the lyrics and translate them.",
            OutlineResultNodeId = "outline-result",
            ResultStepId = "step2",
            Steps =
            [
                new LowLevelStep
                {
                    Id = "step1",
                    OutlineNodeId = "discover-lyrics",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "search",
                    Purpose = "Find candidate lyrics pages."
                },
                new LowLevelStep
                {
                    Id = "step2",
                    OutlineNodeId = "outline-result",
                    Kind = LowLevelStepKinds.Llm,
                    CapabilityId = "translate",
                    Purpose = "Translate the lyrics and format them.",
                    Inputs =
                    [
                        new LowLevelStepInput
                        {
                            Name = "lyrics",
                            Source = new LowLevelInputSource
                            {
                                Kind = LowLevelInputSourceKinds.StepOutputPort,
                                StepId = "step1",
                                Port = "results",
                                Mode = LowLevelInputModes.Value
                            }
                        }
                    ]
                }
            ]
        };

    private static RuntimePlan CreateRuntimePlan() =>
        new()
        {
            Goal = "Find the lyrics and translate them.",
            ResultStepId = "step2",
            ResultPort = "translationTable",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "step1",
                    Kind = "tool",
                    CapabilityId = "search",
                    Purpose = "Find candidate lyrics pages.",
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "results",
                            SemanticType = "search_results"
                        }
                    ]
                },
                new RuntimeStep
                {
                    Id = "step2",
                    Kind = "llm",
                    CapabilityId = "translate",
                    Purpose = "Translate the lyrics and format them.",
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["lyrics"] = new()
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$step1.results",
                            Mode = "value"
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "translationTable",
                            SemanticType = "translation_table"
                        }
                    ]
                }
            ]
        };
}
