using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.Client.Components.Planning;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;

namespace ChatClient.Tests;

public sealed class PlanningDataflowTests
{
    private const string SearchToolName = "mock-web:search";
    private const string MergePagesToolName = "mock-web:merge-pages";
    private const string ObjectToolName = "mock-web:object";

    [Fact]
    public void PlanValidator_RejectsBindingModeFlatten()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user.",
            Steps =
            [
                new PlanStep
                {
                    Id = "search",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuums")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Summarize the evidence.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Ref("$search.results", "flatten")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanValidator.ValidateOrThrow(plan, [CreateSearchDescriptor()]));

        Assert.Contains("mode", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'value' or 'map'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsOrphanNonFinalStep()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchA",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum a")
                    }
                },
                new PlanStep
                {
                    Id = "searchB",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum b")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Summarize the evidence.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Ref("$searchA.results", type: "array<object>")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanValidator.ValidateOrThrow(plan, [CreateSearchDescriptor()]));

        Assert.Contains("searchB", exception.Message, StringComparison.Ordinal);
        Assert.Contains("downstream consumer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanValidator_AcceptsConcatBinding()
    {
        var plan = CreateConcatPlan();

        PlanValidator.ValidateOrThrow(
            plan,
            [CreateSearchDescriptor(), CreateMergePagesDescriptor()]);
    }

    [Fact]
    public void PlanValidator_RejectsConcatBindingWithMapMode()
    {
        var plan = CreateConcatPlan();
        plan.Steps[2].In["pages"] = Concat(
            Ref("$searchA.results", "map"),
            Ref("$searchB.results"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanValidator.ValidateOrThrow(
                plan,
                [CreateSearchDescriptor(), CreateMergePagesDescriptor()]));

        Assert.Contains("concat", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mode='value'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsConcatBindingWhenSourceIsNotArray()
    {
        var plan = new PlanDefinition
        {
            Goal = "Merge evidence.",
            Steps =
            [
                new PlanStep
                {
                    Id = "objectSource",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = ObjectToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["value"] = JsonValue.Create("single")
                    }
                },
                new PlanStep
                {
                    Id = "search",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuums")
                    }
                },
                new PlanStep
                {
                    Id = "merge",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = MergePagesToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Concat(
                            [
                                Ref("$objectSource"),
                                Ref("$search.results")
                            ],
                            type: "array<object>")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanValidator.ValidateOrThrow(
                plan,
                [CreateObjectDescriptor(), CreateSearchDescriptor(), CreateMergePagesDescriptor()]));

        Assert.Contains("does not resolve to an array", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanExecutor_ConcatenatesConcatBindingSources()
    {
        var plan = CreateConcatPlan();
        plan.Steps[0].Status = PlanStepStatuses.Done;
        plan.Steps[0].Result = CreateSearchResult("robot vacuum a", "A1", "A2");
        plan.Steps[1].Status = PlanStepStatuses.Done;
        plan.Steps[1].Result = CreateSearchResult("robot vacuum b", "B1");

        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateSearchDescriptor(), CreateMergePagesDescriptor()]),
            new ThrowingAgentStepRunner());

        var result = await executor.ExecuteAsync(plan);

        Assert.False(result.HasErrors);
        Assert.Equal(PlanStepStatuses.Done, plan.Steps[2].Status);
        Assert.NotNull(plan.Steps[2].Result);
        Assert.Equal(3, plan.Steps[2].Result!.Value.GetProperty("count").GetInt32());
        var titles = plan.Steps[2].Result!.Value.GetProperty("titles").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new string?[] { "A1", "A2", "B1" }, titles);
    }

    [Fact]
    public void GoalVerifier_ReplansWhenPlanContainsOrphanTerminalStep()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchA",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum a")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = CreateSearchResult("robot vacuum a", "A1")
                },
                new PlanStep
                {
                    Id = "searchB",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum b")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = CreateSearchResult("robot vacuum b", "B1")
                },
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Summarize the evidence.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Ref("$searchA.results", type: "array<object>")
                    },
                    Out = StringOut(),
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement("A1 is better.")
                }
            ]
        };

        var verdict = new GoalVerifier().Check(plan, new ExecutionResult());

        Assert.Equal(GoalAction.Replan, verdict.Action);
        Assert.Contains("searchB", verdict.Missing);
    }

    [Fact]
    public void PlanningGraphLinkProjection_LinksResultOnlyFromLastStep()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchA",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum a")
                    }
                },
                new PlanStep
                {
                    Id = "searchB",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum b")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Summarize the evidence.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Ref("$searchA.results", type: "array<object>")
                    },
                    Out = StringOut()
                }
            ]
        };

        var descriptors = PlanningGraphLinkProjection.Build(
            plan.Steps,
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement("done")));

        var resultLink = Assert.Single(descriptors, descriptor => descriptor.Kind == PlanningGraphLinkKind.Result);
        Assert.Equal("answer", resultLink.SourceId);
    }

    private static PlanDefinition CreateConcatPlan() =>
        new()
        {
            Goal = "Merge evidence.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchA",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum a")
                    }
                },
                new PlanStep
                {
                    Id = "searchB",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuum b")
                    }
                },
                new PlanStep
                {
                    Id = "merge",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = MergePagesToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Concat(
                            [
                                Ref("$searchA.results"),
                                Ref("$searchB.results")
                            ],
                            type: "array<object>")
                    }
                }
            ]
        };

    private static JsonElement CreateSearchResult(string query, params string[] titles) =>
        JsonSerializer.SerializeToElement(new
        {
            query,
            results = titles.Select((title, index) => new
            {
                url = $"https://example.com/{query.Replace(' ', '-')}/{index}",
                title
            }).ToArray()
        });

    private static AppToolDescriptor CreateSearchDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "search",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" }
                  },
                  "required": ["query"]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" },
                    "results": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "url": { "type": "string" },
                          "title": { "type": "string" }
                        },
                        "required": ["url", "title"]
                      }
                    }
                  },
                  "required": ["query", "results"]
                }
                """,
            execute: arguments => new
            {
                query = GetRequiredString(arguments, "query"),
                results = new[]
                {
                    new { url = "https://example.com/a1", title = "A1" }
                }
            });

    private static AppToolDescriptor CreateMergePagesDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "merge-pages",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "pages": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "url": { "type": "string" },
                          "title": { "type": "string" }
                        },
                        "required": ["url", "title"]
                      }
                    }
                  },
                  "required": ["pages"]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "count": { "type": "integer" },
                    "titles": {
                      "type": "array",
                      "items": { "type": "string" }
                    }
                  },
                  "required": ["count", "titles"]
                }
                """,
            execute: arguments =>
            {
                var pages = GetRequiredArray(arguments, "pages");
                var titles = pages
                    .Select(page => GetRequiredString(page, "title"))
                    .ToArray();
                return new
                {
                    count = titles.Length,
                    titles
                };
            });

    private static AppToolDescriptor CreateObjectDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "object",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "value": { "type": "string" }
                  },
                  "required": ["value"]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "value": { "type": "string" }
                  },
                  "required": ["value"]
                }
                """,
            execute: arguments => new
            {
                value = GetRequiredString(arguments, "value")
            });

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
            InputSchema: ParseJson(inputSchemaJson),
            OutputSchema: ParseJson(outputSchemaJson),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: (arguments, _) => Task.FromResult(execute(arguments)));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string GetRequiredString(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Expected '{key}' to be a non-empty string.");

        return text;
    }

    private static string GetRequiredString(Dictionary<string, object?> page, string key, bool allowNull = false)
    {
        if (!page.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text))
        {
            if (allowNull)
                return string.Empty;

            throw new InvalidOperationException($"Expected '{key}' to be a non-empty string.");
        }

        return text;
    }

    private static List<Dictionary<string, object?>> GetRequiredArray(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is not List<object?> list)
            throw new InvalidOperationException($"Expected '{key}' to be an array.");

        return list
            .Select(item => item as Dictionary<string, object?> ?? throw new InvalidOperationException($"Expected '{key}' items to be objects."))
            .ToList();
    }

    private static JsonNode Ref(string value, string mode = "value", string? type = null)
    {
        var binding = new JsonObject
        {
            ["from"] = value,
            ["mode"] = mode
        };

        if (!string.IsNullOrWhiteSpace(type))
            binding["type"] = type;

        return binding;
    }

    private static JsonNode Concat(params JsonNode[] bindings) =>
        Concat(bindings, type: null);

    private static JsonNode Concat(IEnumerable<JsonNode> bindings, string? type)
    {
        var concat = new JsonObject
        {
            ["concat"] = new JsonArray(bindings.Select(binding => binding.DeepClone()).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(type))
            concat["type"] = type;

        return concat;
    }

    private static PlanStepOutputContract StringOut() =>
        new()
        {
            Format = PlanStepOutputFormats.String,
            Aggregate = PlanStepOutputAggregates.Single
        };

    private sealed class ThrowingAgentStepRunner : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Agent execution is not expected in this test.");
    }
}
