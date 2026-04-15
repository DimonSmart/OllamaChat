using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Scenarios;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Tests;

public sealed class ThreeLayerPlannerSmokeTests
{
    [Fact]
    public async Task Experiment_WithMockTools_CompilesAndExecutes()
    {
        var scenario = VacuumMopUnder600Scenario.Create();
        var tools = ThreeLayerTestRuntimeFactory.CreateMockWebTools();
        var experiment = new ThreeLayerPlanningExperiment(new StubExperimentLlmClient(scenario), tools);

        var run = await experiment.RunAsync(scenario, runIndex: 1);

        Assert.True(run.OutlineValid);
        Assert.True(run.LowLevelValid);
        Assert.True(run.RuntimeCompiled);
        Assert.True(run.RuntimeExecuted);
        Assert.Equal("executed", run.Status);
        Assert.Equal("string", run.FinalArtifactType);
        Assert.Contains("Vacuum A", run.FinalOutputJson ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class StubExperimentLlmClient(ThreeLayerExperimentScenario scenario) : IExperimentLlmClient
    {
        public Task<PlanningJsonGenerationResult<T>> GenerateJsonAsync<T>(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            object value = agentName switch
            {
                "outline_planner" => BuildOutlinePlan(scenario),
                "low_level_planner" => BuildLowLevelPlan(scenario),
                _ => throw new InvalidOperationException($"Unsupported planner agent '{agentName}'.")
            };

            return Task.FromResult(new PlanningJsonGenerationResult<T>
            {
                Result = (T)value,
                RawResponse = ExperimentJson.SerializeIndented(value),
                RawJson = ExperimentJson.ToNode(value)
            });
        }

        public Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            JsonNode resultNode = agentName switch
            {
                "runtime_s_extract" => BuildExtractOutput(userPrompt),
                "runtime_s_filter" => BuildFilterOutput(userPrompt),
                "runtime_s_answer" => JsonValue.Create("Vacuum A is the best match because it stays under 600 EUR and includes mop support.")!,
                _ => throw new InvalidOperationException($"Unsupported runtime agent '{agentName}'.")
            };

            return Task.FromResult(ResultEnvelope<JsonElement?>.Success(ExperimentJson.ToElement(resultNode)));
        }

        private static OutlinePlan BuildOutlinePlan(ThreeLayerExperimentScenario scenario) =>
            new()
            {
                Goal = "Recommend a robot vacuum mop under 600 EUR.",
                ResultNodeId = "n_answer",
                RequiredDeliverables = [.. scenario.RequiredDeliverables],
                Nodes =
                [
                    new OutlineNode
                    {
                        Id = "n_search",
                        Kind = OutlineNodeKinds.Discover,
                        Purpose = "Find candidate product pages.",
                        Outputs = [new OutlineNodeOutput { Name = "pages", SemanticType = "candidate_page[]" }]
                    },
                    new OutlineNode
                    {
                        Id = "n_download",
                        Kind = OutlineNodeKinds.Acquire,
                        Purpose = "Download candidate product pages.",
                        DependsOn = ["n_search"],
                        Inputs = [new OutlineNodeInput { Name = "pages", SemanticType = "candidate_page[]", FromNodeId = "n_search" }],
                        Outputs = [new OutlineNodeOutput { Name = "documents", SemanticType = "downloaded_document[]" }]
                    },
                    new OutlineNode
                    {
                        Id = "n_extract",
                        Kind = OutlineNodeKinds.Extract,
                        Purpose = "Extract price and mop support facts.",
                        DependsOn = ["n_download"],
                        Inputs = [new OutlineNodeInput { Name = "documents", SemanticType = "downloaded_document[]", FromNodeId = "n_download" }],
                        Outputs = [new OutlineNodeOutput { Name = "records", SemanticType = "product_candidate[]" }]
                    },
                    new OutlineNode
                    {
                        Id = "n_filter",
                        Kind = OutlineNodeKinds.Filter,
                        Purpose = "Filter to products under 600 EUR with mop support.",
                        DependsOn = ["n_extract"],
                        Inputs = [new OutlineNodeInput { Name = "records", SemanticType = "product_candidate[]", FromNodeId = "n_extract" }],
                        Outputs = [new OutlineNodeOutput { Name = "filtered", SemanticType = "product_candidate[]" }]
                    },
                    new OutlineNode
                    {
                        Id = "n_answer",
                        Kind = OutlineNodeKinds.Answer,
                        Purpose = "Write the final recommendation.",
                        DependsOn = ["n_filter"],
                        Inputs = [new OutlineNodeInput { Name = "candidates", SemanticType = "product_candidate[]", FromNodeId = "n_filter" }],
                        Outputs = [new OutlineNodeOutput { Name = "answer", SemanticType = "recommendation_text" }]
                    }
                ]
            };

        private static LowLevelPlan BuildLowLevelPlan(ThreeLayerExperimentScenario scenario) =>
            new()
            {
                Goal = "Recommend a robot vacuum mop under 600 EUR.",
                OutlineResultNodeId = "n_answer",
                ResultStepId = "s_answer",
                Steps =
                [
                    new LowLevelStep
                    {
                        Id = "s_search",
                        OutlineNodeId = "n_search",
                        Kind = LowLevelStepKinds.Tool,
                        CapabilityId = "mock-web:search",
                        Purpose = "Search for candidate products.",
                        Inputs =
                        [
                            new LowLevelStepInput
                            {
                                Name = "query",
                                Source = new LowLevelInputSource
                                {
                                    Kind = LowLevelInputSourceKinds.Literal,
                                    Value = JsonValue.Create(scenario.UserQuery)
                                }
                            }
                        ],
                        Outputs = [new LowLevelStepOutput { Name = "results", SemanticType = "candidate_page[]" }],
                        Fanout = LowLevelFanoutModes.Single
                    },
                    new LowLevelStep
                    {
                        Id = "s_download",
                        OutlineNodeId = "n_download",
                        Kind = LowLevelStepKinds.Tool,
                        CapabilityId = "mock-web:download",
                        Purpose = "Download the candidate pages.",
                        Inputs =
                        [
                            new LowLevelStepInput
                            {
                                Name = "page",
                                Source = new LowLevelInputSource
                                {
                                    Kind = LowLevelInputSourceKinds.StepOutputPort,
                                    StepId = "s_search",
                                    Port = "results",
                                    Mode = LowLevelInputModes.Map
                                }
                            }
                        ],
                        Outputs = [new LowLevelStepOutput { Name = "documents", SemanticType = "downloaded_document[]" }],
                        Fanout = LowLevelFanoutModes.PerItem
                    },
                    new LowLevelStep
                    {
                        Id = "s_extract",
                        OutlineNodeId = "n_extract",
                        Kind = LowLevelStepKinds.Llm,
                        Purpose = "Extract structured product candidates.",
                        Inputs =
                        [
                            new LowLevelStepInput
                            {
                                Name = "documents",
                                Source = new LowLevelInputSource
                                {
                                    Kind = LowLevelInputSourceKinds.StepOutputPort,
                                    StepId = "s_download",
                                    Port = "documents",
                                    Mode = LowLevelInputModes.Value
                                }
                            }
                        ],
                        Outputs = [new LowLevelStepOutput { Name = "records", SemanticType = "product_candidate[]" }],
                        Fanout = LowLevelFanoutModes.Single,
                        Out = new LowLevelStepOutputSettings { Format = RuntimeOutputFormats.Json }
                    },
                    new LowLevelStep
                    {
                        Id = "s_filter",
                        OutlineNodeId = "n_filter",
                        Kind = LowLevelStepKinds.Llm,
                        Purpose = "Keep only products with mop support and price under 600 EUR.",
                        Inputs =
                        [
                            new LowLevelStepInput
                            {
                                Name = "records",
                                Source = new LowLevelInputSource
                                {
                                    Kind = LowLevelInputSourceKinds.StepOutputPort,
                                    StepId = "s_extract",
                                    Port = "records",
                                    Mode = LowLevelInputModes.Value
                                }
                            }
                        ],
                        Outputs = [new LowLevelStepOutput { Name = "filtered", SemanticType = "product_candidate[]" }],
                        Fanout = LowLevelFanoutModes.Single,
                        Out = new LowLevelStepOutputSettings { Format = RuntimeOutputFormats.Json }
                    },
                    new LowLevelStep
                    {
                        Id = "s_answer",
                        OutlineNodeId = "n_answer",
                        Kind = LowLevelStepKinds.Llm,
                        Purpose = "Write the final recommendation.",
                        Inputs =
                        [
                            new LowLevelStepInput
                            {
                                Name = "candidates",
                                Source = new LowLevelInputSource
                                {
                                    Kind = LowLevelInputSourceKinds.StepOutputPort,
                                    StepId = "s_filter",
                                    Port = "filtered",
                                    Mode = LowLevelInputModes.Value
                                }
                            }
                        ],
                        Outputs = [new LowLevelStepOutput { Name = "answer", SemanticType = "recommendation_text" }],
                        Fanout = LowLevelFanoutModes.Single,
                        Out = new LowLevelStepOutputSettings { Format = RuntimeOutputFormats.String },
                        IsResult = true
                    }
                ]
            };

        private static JsonNode BuildExtractOutput(string userPrompt)
        {
            using var document = ParseInputDocument(userPrompt);
            var documents = document.RootElement
                .GetProperty("documents")
                .EnumerateArray()
                .Select(static element =>
                {
                    var title = element.GetProperty("title").GetString() ?? string.Empty;
                    var content = element.GetProperty("content").GetString() ?? string.Empty;
                    var price = content.Contains("549", StringComparison.Ordinal) ? 549 : 699;
                    var hasMop = content.Contains("mop support", StringComparison.OrdinalIgnoreCase);
                    return new JsonObject
                    {
                        ["name"] = title,
                        ["priceEur"] = price,
                        ["hasMop"] = hasMop
                    };
                })
                .ToArray();

            return new JsonObject
            {
                ["records"] = new JsonArray(documents)
            };
        }

        private static JsonNode BuildFilterOutput(string userPrompt)
        {
            using var document = ParseInputDocument(userPrompt);
            var records = document.RootElement
                .GetProperty("records")
                .EnumerateArray()
                .Where(static element => element.GetProperty("hasMop").GetBoolean() && element.GetProperty("priceEur").GetInt32() <= 600)
                .Select(static element => JsonNode.Parse(element.GetRawText()))
                .ToArray();

            return new JsonObject
            {
                ["filtered"] = new JsonArray(records)
            };
        }

        private static JsonDocument ParseInputDocument(string userPrompt)
        {
            const string marker = "Inputs:";
            var markerIndex = userPrompt.IndexOf(marker, StringComparison.Ordinal);
            var json = markerIndex >= 0
                ? userPrompt[(markerIndex + marker.Length)..].Trim()
                : throw new InvalidOperationException("Inputs block was not found.");
            return JsonDocument.Parse(json);
        }
    }
}
