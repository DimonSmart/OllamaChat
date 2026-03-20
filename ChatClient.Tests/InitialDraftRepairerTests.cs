using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;
using Microsoft.Extensions.AI;
using Moq;

namespace ChatClient.Tests;

public sealed class InitialDraftRepairerTests
{
    [Fact]
    public async Task LlmPlanner_UsesInitialDraftRepairer_WhenDraftFailsValidation()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var repairedPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(SerializeEnvelope(invalidPlan));
        var repairer = new RecordingInitialDraftRepairer(repairedPlan);
        var planner = new LlmPlanner(chatClient.Object, toolCatalog, initialDraftRepairer: repairer);

        var result = await planner.CreatePlanAsync("Find a robot vacuum.");

        Assert.Equal(1, repairer.CallCount);
        Assert.NotNull(repairer.LastRequest);
        Assert.Equal("tool_input_missing_required", repairer.LastRequest!.ValidationIssue.Code);
        Assert.Equal("Find a robot vacuum.", repairer.LastRequest.UserQuery);
        Assert.Equal("robot vacuums", result.Steps[0].In["query"]?.GetValue<string>());
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LlmPlanner_UsesSingleFallbackGeneration_AfterInitialRepairFails()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var validFallbackPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(
            SerializeEnvelope(invalidPlan),
            SerializeEnvelope(validFallbackPlan));
        var repairer = new ThrowingInitialDraftRepairer(new InvalidOperationException("Repair failed."));
        var planner = new LlmPlanner(chatClient.Object, toolCatalog, initialDraftRepairer: repairer);

        var result = await planner.CreatePlanAsync("Find a robot vacuum.");

        Assert.Equal(1, repairer.CallCount);
        Assert.Equal("robot vacuums", result.Steps[0].In["query"]?.GetValue<string>());
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task LlmPlanner_DoesNotLoopBeyondSingleFallbackGeneration()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(
            SerializeEnvelope(invalidPlan),
            SerializeEnvelope(invalidPlan),
            SerializeEnvelope(CreateValidSearchPlan()));
        var repairer = new ThrowingInitialDraftRepairer(new InvalidOperationException("Repair failed."));
        var planner = new LlmPlanner(chatClient.Object, toolCatalog, initialDraftRepairer: repairer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => planner.CreatePlanAsync("Find a robot vacuum."));

        Assert.Contains("after 2 draft generations", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, repairer.CallCount);
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task LlmInitialDraftRepairer_RepairsInvalidDraft_WithToolCalling()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var repairedPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanReplaceStep,
                new Dictionary<string, object?>
                {
                    ["stepId"] = "search_vacuums",
                    ["step"] = JsonSerializer.SerializeToElement(repairedPlan.Steps[0])
                }),
            CreateTextResponse("Draft repaired."));
        var repairer = new LlmInitialDraftRepairer(chatClient.Object, toolCatalog);

        var result = await repairer.RepairAsync(new InitialDraftRepairRequest
        {
            UserQuery = "Find a robot vacuum.",
            AttemptNumber = 1,
            DraftPlan = invalidPlan,
            ValidationIssue = new PlanValidationIssue(
                "tool_input_missing_required",
                "Tool step 'search_vacuums' is missing required input 'query' for tool 'mock:web:search'.",
                StepId: "search_vacuums",
                InputName: "query",
                ToolName: "mock:web:search")
        });

        Assert.True(PlanValidator.TryValidate(result, toolCatalog.ListTools(), out var issue), issue?.Message);
        Assert.Equal("robot vacuums", result.Steps[0].In["query"]?.GetValue<string>());
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public void PlanEditingSession_ReplaceStep_RejectsLegacyNewStepAlias()
    {
        var session = new PlanEditingSession(CreateInvalidSearchPlan());
        var replacementStep = CreateValidSearchPlan().Steps[0];
        var actionInput = JsonSerializer.SerializeToNode(new
        {
            newStep = replacementStep
        })!.AsObject();

        var result = session.ExecuteAction("plan.replaceStep", actionInput);

        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.Contains("Action input 'stepId' is required.", result["error"]?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task LlmInitialDraftRepairer_Retries_WhenModelSkipsToolUse()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var repairedPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(
            CreateTextResponse("Draft repaired."),
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanReplaceStep,
                new Dictionary<string, object?>
                {
                    ["stepId"] = "search_vacuums",
                    ["step"] = JsonSerializer.SerializeToElement(repairedPlan.Steps[0])
                },
                callId: "call-2"),
            CreateTextResponse("Draft repaired."));
        var repairer = new LlmInitialDraftRepairer(chatClient.Object, toolCatalog);

        var result = await repairer.RepairAsync(new InitialDraftRepairRequest
        {
            UserQuery = "Find a robot vacuum.",
            AttemptNumber = 1,
            DraftPlan = invalidPlan,
            ValidationIssue = new PlanValidationIssue(
                "tool_input_missing_required",
                "Tool step 'search_vacuums' is missing required input 'query' for tool 'mock:web:search'.",
                StepId: "search_vacuums",
                InputName: "query",
                ToolName: "mock:web:search")
        });

        Assert.True(PlanValidator.TryValidate(result, toolCatalog.ListTools(), out var issue), issue?.Message);
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task LlmReplanner_RepairsFailedPlan_WithToolCalling()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var repairedPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanReplaceStep,
                new Dictionary<string, object?>
                {
                    ["stepId"] = "search_vacuums",
                    ["step"] = JsonSerializer.SerializeToElement(repairedPlan.Steps[0])
                }),
            CreateTextResponse("Replan repaired."));
        var replanner = new LlmReplanner(chatClient.Object, toolCatalog);

        var result = await replanner.ReplanAsync(new PlannerReplanRequest
        {
            UserQuery = "Find a robot vacuum.",
            AttemptNumber = 1,
            Plan = invalidPlan,
            ExecutionResult = new ExecutionResult
            {
                StepTraces =
                [
                    new StepExecutionTrace
                    {
                        StepId = "search_vacuums",
                        Success = false,
                        ErrorCode = "tool_error",
                        ErrorMessage = "Execution failed."
                    }
                ]
            },
            GoalVerdict = new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Execution has failed steps."
            }
        });

        Assert.True(PlanValidator.TryValidate(result, toolCatalog.ListTools(), out var issue), issue?.Message);
        Assert.Equal("robot vacuums", result.Steps[0].In["query"]?.GetValue<string>());
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private static PlanDefinition CreateInvalidSearchPlan() =>
        new()
        {
            Goal = "Find robot vacuums.",
            Steps =
            [
                new PlanStep
                {
                    Id = "search_vacuums",
                    Tool = "mock:web:search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("https://example.com/robot-vacuums")
                    }
                }
            ]
        };

    private static PlanDefinition CreateValidSearchPlan() =>
        new()
        {
            Goal = "Find robot vacuums.",
            Steps =
            [
                new PlanStep
                {
                    Id = "search_vacuums",
                    Tool = "mock:web:search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuums")
                    }
                }
            ]
        };

    private static Mock<IChatClient> CreateMockChatClient(params string[] jsonResponses) =>
        CreateMockChatClient(jsonResponses.Select(CreateTextResponse).ToArray());

    private static Mock<IChatClient> CreateMockChatClient(params ChatResponse[] responses)
    {
        var queue = new Queue<ChatResponse>(responses);
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);

        chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Dequeue());

        chatClient
            .Setup(client => client.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        chatClient
            .Setup(client => client.GetService(It.IsAny<Type>(), It.IsAny<object?>()))
            .Returns((object?)null);

        chatClient
            .Setup(client => client.Dispose());

        return chatClient;
    }

    private static ChatResponse CreateTextResponse(string json) =>
        new(new ChatMessage(ChatRole.Assistant, json));

    private static ChatResponse CreateToolCallResponse(
        string toolName,
        IDictionary<string, object?> arguments,
        string callId = "call-1") =>
        new(new ChatMessage(
            ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent(callId, toolName, arguments)
            }));

    private static string SerializeEnvelope(object data) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            data,
            error = (object?)null
        });

    private static AppToolDescriptor CreateSearchDescriptor() =>
        new(
            QualifiedName: "mock:web:search",
            ServerName: "mock-web",
            ToolName: "search",
            DisplayName: "search",
            Description: "Search the web.",
            InputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" }
                  },
                  "required": ["query"]
                }
                """),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "results": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "url": { "type": "string" }
                        }
                      }
                    }
                  }
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: static (_, _) => Task.FromResult<object>(new { results = Array.Empty<object>() }));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingInitialDraftRepairer(PlanDefinition repairedPlan) : IInitialDraftRepairer
    {
        public int CallCount { get; private set; }

        public InitialDraftRepairRequest? LastRequest { get; private set; }

        public Task<PlanDefinition> RepairAsync(InitialDraftRepairRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(ClonePlan(repairedPlan));
        }

        private static PlanDefinition ClonePlan(PlanDefinition plan) =>
            JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
            ?? throw new InvalidOperationException("Failed to clone test plan.");
    }

    private sealed class ThrowingInitialDraftRepairer(Exception exceptionToThrow) : IInitialDraftRepairer
    {
        public int CallCount { get; private set; }

        public Task<PlanDefinition> RepairAsync(InitialDraftRepairRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromException<PlanDefinition>(exceptionToThrow);
        }
    }
}
