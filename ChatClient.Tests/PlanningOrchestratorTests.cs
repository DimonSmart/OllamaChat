using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Orchestration;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;

namespace ChatClient.Tests;

public sealed class PlanningOrchestratorTests
{
    [Fact]
    public async Task RunAsync_ReturnsFinalAnswer_WhenFinalAnswerVerifierThrows()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user.",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Tool,
                    Name = "mock:answer",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robots")
                    }
                }
            ]
        };

        var tools = new PlanningToolCatalog(
        [
            CreateDescriptor(
                "mock",
                "answer",
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" }
                  },
                  "required": ["query"]
                }
                """,
                """
                {
                  "type": "object",
                  "properties": {
                    "message": { "type": "string" }
                  },
                  "required": ["message"]
                }
                """,
                _ => new
                {
                    message = "final answer"
                })
        ]);

        var executor = new PlanExecutor(tools, new ThrowingAgentStepRunner());
        var orchestrator = new PlanningOrchestrator(
            new StubPlanner(plan),
            executor,
            new GoalVerifier(),
            maxAttempts: 1,
            finalAnswerVerifier: new ThrowingFinalAnswerVerifier());

        var result = await orchestrator.RunAsync("example");

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
        Assert.Equal("final answer", result.Data.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RunAsync_ReturnsPartialExecutionFailure_WhenPlanEndsWithPartialDataAndVerificationStillFails()
    {
        var plan = new PlanDefinition
        {
            Goal = "Collect items and answer the user.",
            Steps =
            [
                new PlanStep
                {
                    Id = "seed",
                    Kind = PlanStepKinds.Tool,
                    Name = "mock:seed",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robots")
                    }
                },
                new PlanStep
                {
                    Id = "collect",
                    Kind = PlanStepKinds.Tool,
                    Name = "mock:collect",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["item"] = Ref("$seed.items", "map")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.Json,
                        Aggregate = PlanStepOutputAggregates.Collect,
                        Schema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            required = new[] { "value" },
                            properties = new
                            {
                                value = new
                                {
                                    type = "string"
                                }
                            }
                        })
                    }
                }
            ]
        };

        var tools = new PlanningToolCatalog(
        [
            CreateDescriptor(
                "mock",
                "seed",
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" }
                  },
                  "required": ["query"]
                }
                """,
                """
                {
                  "type": "object",
                  "properties": {
                    "items": {
                      "type": "array",
                      "items": { "type": "string" }
                    }
                  },
                  "required": ["items"]
                }
                """,
                _ => new
                {
                    items = new[] { "a", "b", "c" }
                }),
            CreateDescriptor(
                "mock",
                "collect",
                """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string" }
                  },
                  "required": ["item"]
                }
                """,
                """
                {
                  "type": "object",
                  "properties": {
                    "value": { "type": "string" }
                  },
                  "required": ["value"]
                }
                """,
                arguments =>
                {
                    var item = GetRequiredString(arguments, "item");
                    if (string.Equals(item, "b", StringComparison.Ordinal))
                        throw new InvalidOperationException("Synthetic mapped failure.");

                    return new
                    {
                        value = item.ToUpperInvariant()
                    };
                })
        ]);

        var executor = new PlanExecutor(tools, new ThrowingAgentStepRunner());
        var orchestrator = new PlanningOrchestrator(
            new StubPlanner(plan),
            executor,
            new GoalVerifier(),
            maxAttempts: 1,
            finalAnswerVerifier: new StubFinalAnswerVerifier(new FinalAnswerVerificationResult
            {
                IsAnswer = false,
                Reason = "The plan ended with collected raw data instead of a final answer.",
                Missing = ["final_answer"]
            }));

        var result = await orchestrator.RunAsync("example");

        Assert.False(result.Ok);
        Assert.Equal("partial_execution", result.Error?.Code);
        Assert.Equal(
            "Execution completed with partial data: one or more aggregated steps failed for some inputs, so the result may be incomplete.",
            result.Error?.Message);

        Assert.NotNull(result.Error?.Details);
        var details = result.Error!.Details!.Value;
        Assert.True(details.GetProperty("hasPartialData").GetBoolean());
        Assert.Equal("The plan ended with collected raw data instead of a final answer.", details.GetProperty("reason").GetString());

        var partialSteps = details.GetProperty("partialSteps").EnumerateArray().ToList();
        var partialStep = Assert.Single(partialSteps);
        Assert.Equal("collect", partialStep.GetProperty("id").GetString());
        Assert.Equal("partial_failure", partialStep.GetProperty("code").GetString());

        var lastAvailableResult = details.GetProperty("lastAvailableResult");
        Assert.Equal(JsonValueKind.Array, lastAvailableResult.ValueKind);
        Assert.Equal(2, lastAvailableResult.GetArrayLength());
    }

    private static AppToolDescriptor CreateDescriptor(
        string serverName,
        string toolName,
        string inputSchemaJson,
        string outputSchemaJson,
        Func<Dictionary<string, object?>, object> execute) =>
        new(
            QualifiedName: $"{serverName}:{toolName}",
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: toolName,
            InputSchema: ParseJsonElement(inputSchemaJson),
            OutputSchema: ParseJsonElement(outputSchemaJson),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: (arguments, _) => Task.FromResult(execute(arguments)));

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string GetRequiredString(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Expected argument '{key}' to be a non-empty string.");

        return text;
    }

    private static JsonNode Ref(string value, string mode) => new JsonObject
    {
        ["from"] = value,
        ["mode"] = mode
    };

    private sealed class StubPlanner(PlanDefinition plan) : IPlanner
    {
        public Task<PlanDefinition> CreatePlanAsync(string userQuery, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
                ?? throw new InvalidOperationException("Failed to clone test plan."));
    }

    private sealed class StubFinalAnswerVerifier(FinalAnswerVerificationResult result) : IFinalAnswerVerifier
    {
        public Task<FinalAnswerVerificationResult> VerifyAsync(string userQuery, JsonElement? answer, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingFinalAnswerVerifier : IFinalAnswerVerifier
    {
        public Task<FinalAnswerVerificationResult> VerifyAsync(string userQuery, JsonElement? answer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic verifier failure.");
    }

    private sealed class ThrowingAgentStepRunner : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Agent execution is not expected in this test.");
    }
}
