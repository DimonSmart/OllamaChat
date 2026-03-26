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
        var chatClient = CreateMockChatClient(SerializePlan(invalidPlan));
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
            SerializePlan(invalidPlan),
            SerializePlan(validFallbackPlan));
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
            SerializePlan(invalidPlan),
            SerializePlan(invalidPlan),
            SerializePlan(CreateValidSearchPlan()));
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
    public async Task LlmPlanner_UsesTypedPlanResponse_AndDraftPrompt()
    {
        var validPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        IEnumerable<ChatMessage>? capturedMessages = null;
        ChatOptions? capturedOptions = null;
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, options, _) =>
            {
                capturedMessages = messages.ToArray();
                capturedOptions = options;
            })
            .ReturnsAsync(CreateTextResponse(SerializePlan(validPlan)));
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
        var planner = new LlmPlanner(chatClient.Object, toolCatalog);

        var result = await planner.CreatePlanAsync("Find a robot vacuum.");

        Assert.Equal(validPlan.Goal, result.Goal);
        var messages = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(capturedMessages);
        var systemPrompt = capturedOptions?.Instructions
            ?? messages.SingleOrDefault(message => message.Role == ChatRole.System)?.Text;

        Assert.NotNull(systemPrompt);
        Assert.DoesNotContain("JSON envelope", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ok=true", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s, res, and err", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Few-shot", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tool steps must not declare out", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("out must include format", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("out.schema is optional", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("out must include format ('json' or 'string') and aggregate", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("concat", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never use 'flatten' as a binding mode", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not assume a fixed workflow", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("actual description, schema, and compatibility metadata", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserve those exact records", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("directly compatible with another tool input", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not reduce them to lossy summaries first", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a limit is only a maximum", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum supported set plus an insufficiency field", systemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LlmPlanner_NormalizesDraftContracts_BeforeValidation()
    {
        var draft = new PlanDefinition
        {
            Goal = "Find robot vacuums.",
            Steps =
            [
                new PlanStep
                {
                    Id = "search_vacuums",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock:web:search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuums")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.Json,
                        Schema = ParseJson(
                            """
                            {
                              "type": "object",
                              "properties": {
                                "bogus": { "type": "string" }
                              }
                            }
                            """)
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Kind = "LLM",
                    CapabilityId = "   ",
                    SystemPrompt = "Summarize the evidence.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = new JsonObject
                        {
                            ["from"] = "$search_vacuums.results",
                            ["mode"] = "value"
                        }
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = "STRING"
                    }
                }
            ]
        };
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(SerializePlan(draft));
        var planner = new LlmPlanner(chatClient.Object, toolCatalog);

        var result = await planner.CreatePlanAsync("Find a robot vacuum.");

        Assert.Null(result.Steps[0].Out);
        Assert.Equal(PlanStepKinds.Llm, result.Steps[1].Kind);
        Assert.Null(result.Steps[1].CapabilityId);
        Assert.Equal(PlanStepOutputFormats.String, result.Steps[1].Out?.Format);
        var binding = Assert.IsType<JsonObject>(result.Steps[1].In["pages"]);
        Assert.Equal("$search_vacuums.results", binding["from"]?.GetValue<string>());
        Assert.Equal("value", binding["mode"]?.GetValue<string>());
        Assert.True(PlanValidator.TryValidate(result, toolCatalog.ListTools(), out var issue), issue?.Message);
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
    public async Task LlmInitialDraftRepairer_PromptRequiresPreservingSourceRecords()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var repairedPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        IEnumerable<ChatMessage>? capturedMessages = null;
        ChatOptions? capturedOptions = null;
        var queue = new Queue<ChatResponse>(
        [
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanReplaceStep,
                new Dictionary<string, object?>
                {
                    ["stepId"] = "search_vacuums",
                    ["step"] = JsonSerializer.SerializeToElement(repairedPlan.Steps[0])
                }),
            CreateTextResponse("Draft repaired.")
        ]);
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, options, _) =>
            {
                capturedMessages ??= messages.ToArray();
                capturedOptions ??= options;
            })
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
        var messages = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(capturedMessages);
        var systemPrompt = capturedOptions?.Instructions
            ?? messages.SingleOrDefault(message => message.Role == ChatRole.System)?.Text;

        Assert.NotNull(systemPrompt);
        Assert.Contains("instead of assuming a fixed workflow", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserving the required source records", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("directly compatible with a downstream tool input", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not solve a count gap by only weakening the final writer", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("increasing a tool limit", systemPrompt, StringComparison.OrdinalIgnoreCase);
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
                        Outcome = StepTraceOutcome.Failed,
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

    [Fact]
    public async Task LlmReplanner_DoesNotAcceptValidationOnlyRound_AsRepairedPlan()
    {
        var invalidPlan = CreateInvalidSearchPlan();
        var repairedPlan = CreateValidSearchPlan();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        var chatClient = CreateMockChatClient(
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanValidateDraft,
                new Dictionary<string, object?>()),
            CreateTextResponse("Replan repaired."),
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanReplaceStep,
                new Dictionary<string, object?>
                {
                    ["stepId"] = "search_vacuums",
                    ["step"] = JsonSerializer.SerializeToElement(repairedPlan.Steps[0])
                },
                callId: "call-2"),
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
                        Outcome = StepTraceOutcome.Failed,
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
            Times.Exactly(4));
    }

    [Fact]
    public async Task LlmReplanner_CanRemoveDeadStep_AndPromptRequiresDiagnosisNote()
    {
        var invalidPlan = CreatePlanWithUnusedFinalStep();
        var toolCatalog = new PlanningToolCatalog([CreateSearchDescriptor()]);
        IEnumerable<ChatMessage>? capturedMessages = null;
        ChatOptions? capturedOptions = null;
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var queue = new Queue<ChatResponse>(
        [
            CreateToolCallResponse(
                PlanningAgentToolNames.PlanRemoveStep,
                new Dictionary<string, object?>
                {
                    ["stepId"] = "extra_search"
                }),
            CreateTextResponse("Diagnosis: the trailing extra_search step made the earlier search output unused. Changes: removed the redundant extra_search step.")
        ]);

        chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, options, _) =>
            {
                capturedMessages ??= messages.ToArray();
                capturedOptions ??= options;
            })
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
        var replanner = new LlmReplanner(chatClient.Object, toolCatalog);

        var result = await replanner.ReplanAsync(new PlannerReplanRequest
        {
            UserQuery = "Find a robot vacuum.",
            AttemptNumber = 1,
            Plan = invalidPlan,
            ExecutionResult = new ExecutionResult(),
            GoalVerdict = new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Plan is structurally invalid."
            }
        });

        Assert.True(PlanValidator.TryValidate(result, toolCatalog.ListTools(), out var issue), issue?.Message);
        Assert.Equal(["search_vacuums"], result.Steps.Select(step => step.Id).ToArray());

        var messages = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(capturedMessages);
        var systemPrompt = capturedOptions?.Instructions
            ?? messages.SingleOrDefault(message => message.Role == ChatRole.System)?.Text;

        Assert.NotNull(systemPrompt);
        Assert.Contains(PlanningAgentToolNames.PlanRemoveStep, systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Diagnosis:", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("First identify what is actually wrong", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("instead of assuming a fixed workflow", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("increase a tool limit", systemPrompt, StringComparison.OrdinalIgnoreCase);
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
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock:web:search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("https://example.com/robot-vacuums")
                    }
                }
            ]
        };

    private static PlanDefinition CreatePlanWithUnusedFinalStep() =>
        new()
        {
            Goal = "Find robot vacuums.",
            Steps =
            [
                CreateValidSearchPlan().Steps[0],
                new PlanStep
                {
                    Id = "extra_search",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock:web:search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("unused")
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
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock:web:search",
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

    private static string SerializePlan(PlanDefinition plan) =>
        PlanJsonProfiles.SerializeCompact(plan, PlanModelProfile.Draft);

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




