using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.Services;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class ThreeLayerRuntimeCompilerTests
{
    [Fact]
    public void Compile_TreatsPluralSemanticHintsAsCollections_ForMappedDownloadInputs()
    {
        var plan = new LowLevelPlan
        {
            Goal = "Recommend a robot vacuum with mopping under 600 EUR.",
            OutlineResultNodeId = "n_answer",
            ResultStepId = "step3",
            Steps =
            [
                new LowLevelStep
                {
                    Id = "step1",
                    OutlineNodeId = "n_discover",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Collect candidate page references from search evidence.",
                    Outputs =
                    [
                        new LowLevelStepOutput
                        {
                            Name = "candidateReferences",
                            SemanticType = "searchResultReferences"
                        }
                    ],
                    Fanout = LowLevelFanoutModes.Single,
                    Out = new LowLevelStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new LowLevelStep
                {
                    Id = "step2",
                    OutlineNodeId = "n_acquire",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "binding:11111111111111111111111111111111:download",
                    Purpose = "Download the candidate pages.",
                    Inputs =
                    [
                        new LowLevelStepInput
                        {
                            Name = "page",
                            Source = new LowLevelInputSource
                            {
                                Kind = LowLevelInputSourceKinds.StepOutputPort,
                                StepId = "step1",
                                Port = "candidateReferences",
                                Mode = LowLevelInputModes.Map
                            }
                        }
                    ],
                    Outputs =
                    [
                        new LowLevelStepOutput
                        {
                            Name = "documents",
                            SemanticType = "downloadedDocuments"
                        }
                    ],
                    Fanout = LowLevelFanoutModes.PerItem
                },
                new LowLevelStep
                {
                    Id = "step3",
                    OutlineNodeId = "n_answer",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Write the final recommendation.",
                    Inputs =
                    [
                        new LowLevelStepInput
                        {
                            Name = "documents",
                            Source = new LowLevelInputSource
                            {
                                Kind = LowLevelInputSourceKinds.StepOutputPort,
                                StepId = "step2",
                                Port = "documents",
                                Mode = LowLevelInputModes.Value
                            }
                        }
                    ],
                    Outputs =
                    [
                        new LowLevelStepOutput
                        {
                            Name = "answer",
                            SemanticType = "recommendationText"
                        }
                    ],
                    Fanout = LowLevelFanoutModes.Single,
                    Out = new LowLevelStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.String
                    },
                    IsResult = true
                }
            ]
        };

        var compiler = new RuntimePlannerCompiler([CreateBoundWebDownloadDescriptor()]);

        var result = compiler.Compile(plan);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        var runtimePlan = result.Plan;
        Assert.NotNull(runtimePlan);
        var discoverStep = Assert.Single(runtimePlan!.Steps, step => step.Id == "step1");
        var acquireStep = Assert.Single(runtimePlan.Steps, step => step.Id == "step2");

        Assert.Equal("searchResultReferences[]", discoverStep.Outputs.Single().SemanticType);
        Assert.Equal(LowLevelInputModes.Map, acquireStep.In["page"].Mode);
    }

    private static AppToolDescriptor CreateBoundWebDownloadDescriptor() =>
        new(
            QualifiedName: "binding:11111111111111111111111111111111:download",
            ServerName: "Built-in Web MCP Server",
            ToolName: "download",
            DisplayName: "download",
            Description: "Download a single page from a page reference.",
            InputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "page": { "type": "object" },
                    "url": { "type": "string" }
                  }
                }
                """),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" },
                    "content": { "type": "string" }
                  }
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: static (_, _) => Task.FromResult<object>(new object()),
            BaseQualifiedName: "built-in-web:download",
            BaseServerName: "built-in-web");

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
