using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Headers;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ModelContextProtocol.Protocol;

namespace ChatClient.Tests;

public class PlanningRuntimeContractsTests
{
    private const string SearchToolName = "mock-web:search";
    private const string DownloadToolName = "mock-web:download";
    private const string PairToolName = "mock-web:pair";

    [Fact]
    public void PlanValidator_RejectsPromptRefsInsideAgentPrompts()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user question.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("best robot vacuum")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Use $searchPages.results to answer the user.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = Ref("$searchPages.results")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must not embed step refs inside systemPrompt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsLegacyStringRefsInsideInputs()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download search result pages.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("$searchPages.results[].url")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("uses legacy string ref syntax", exception.Message, StringComparison.Ordinal);
        Assert.Contains("{\"from\":\"$searchPages.results[].url\",\"mode\":\"value\"}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanNormalizer_DoesNotRewriteLegacyBindings_RemovesToolOut_AndKeepsMissingJsonSchemaOptional()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract product facts.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    },
                    Out = StringOut()
                },
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = "LLM",
                    CapabilityId = "",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract the requested fields.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = JsonValue.Create("$searchPages.results[0]")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.Json
                    }
                }
            ]
        };

        PlanNormalizer.Normalize(plan, [CreateStaticSearchDescriptor()]);

        Assert.Null(plan.Steps[0].Out);
        Assert.Equal(PlanStepKinds.Llm, plan.Steps[1].Kind);
        Assert.Null(plan.Steps[1].CapabilityId);
        var legacyBinding = Assert.IsAssignableFrom<JsonValue>(plan.Steps[1].In["page"]);
        Assert.Equal("$searchPages.results[0]", legacyBinding.GetValue<string>());
        Assert.False(plan.Steps[1].Out?.Schema.HasValue ?? false);
        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));
        Assert.Contains("uses legacy string ref syntax", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsSingleBraceTemplatePlaceholderInsidePrompts()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user question.",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Summarize the selected model {model_name}. Return JSON only.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["modelName"] = JsonValue.Create("Eufy X10 Pro Omni")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must not contain unresolved template placeholders", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsBracketStyleTemplatePlaceholderInsidePrompts()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user question.",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "Summarize the selected model. Return JSON only.",
                    UserPrompt = "Compare [[model_a]] against [[model_b]].",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["modelA"] = JsonValue.Create("Eufy X10 Pro Omni"),
                        ["modelB"] = JsonValue.Create("Roborock Qrevo Curv")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must not contain unresolved template placeholders", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanJsonProfiles_DraftSerialization_ExcludesRuntimeFields()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download search result pages.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement(new { ok = true }),
                    Error = new PlanStepError
                    {
                        Code = "ignored",
                        Message = "ignored"
                    }
                }
            ]
        };

        var json = PlanJsonProfiles.SerializeCompact(plan, PlanModelProfile.Draft);

        Assert.Contains("\"goal\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"s\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"res\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"err\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanJsonProfiles_DraftDeserialization_AllowsOutputContractWithoutFormat_ForValidatorToCatch()
    {
        var json = """
              {
                "goal": "Answer the user question.",
                "steps": [
                  {
                    "id": "answer",
                    "kind": "llm",
                    "systemPrompt": "Summarize the evidence.",
                    "userPrompt": "Write the final answer.",
                    "in": {
                      "question": "example"
                    },
                  "out": {}
                }
              ]
            }
            """;

        var node = JsonNode.Parse(json);
        var plan = PlanJsonProfiles.Deserialize<PlanDefinition>(node, PlanModelProfile.Draft);

        Assert.NotNull(plan);
        Assert.NotNull(plan.Steps[0].Out);
        Assert.Equal(PlanStepOutputFormats.Json, plan.Steps[0].Out!.Format);
        Assert.True(PlanValidator.TryValidate(plan, tools: null, callableAgents: null, PlanModelProfile.Draft, out var issue), issue?.Message);
    }

    [Fact]
    public void PlanJsonProfiles_DraftDeserialization_AllowsStepWithoutIn_ForValidatorToCatch()
    {
        var json = """
              {
                "goal": "Answer the user question.",
                "steps": [
                  {
                    "id": "answer",
                    "kind": "llm",
                    "systemPrompt": "Summarize the evidence.",
                    "userPrompt": "Write the final answer.",
                    "out": { "format": "string", "aggregate": "single" }
                  }
                ]
            }
            """;

        var node = JsonNode.Parse(json);
        var plan = PlanJsonProfiles.Deserialize<PlanDefinition>(node, PlanModelProfile.Draft);

        Assert.NotNull(plan);
        Assert.False(PlanValidator.TryValidate(plan, tools: null, callableAgents: null, PlanModelProfile.Draft, out var issue));
        Assert.Contains("must declare its inputs in 'in'", issue?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanSanitizer_DraftProfile_ClearsRuntimeFields()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download search result pages.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement(new { ok = true }),
                    Error = new PlanStepError
                    {
                        Code = "failed",
                        Message = "failed"
                    }
                }
            ]
        };

        PlanSanitizer.Sanitize(plan, PlanModelProfile.Draft);

        Assert.Equal(PlanStepStatuses.Todo, plan.Steps[0].Status);
        Assert.Null(plan.Steps[0].Result);
        Assert.Null(plan.Steps[0].Error);
    }

    [Fact]
    public void PlanRuntimeHydrator_CreateRuntimePlan_ResetsRuntimeFields_AndPreservesBindings()
    {
        var source = new PlanDefinition
        {
            Goal = "Answer the user question.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement(new { results = new[] { new { url = "https://example.com" } } })
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
                        ["pages"] = Ref("$searchPages.results")
                    },
                    Out = StringOut(),
                    Status = PlanStepStatuses.Fail,
                    Error = new PlanStepError
                    {
                        Code = "old_error",
                        Message = "old error"
                    }
                }
            ]
        };

        var runtime = PlanRuntimeHydrator.CreateRuntimePlan(source);

        Assert.NotSame(source, runtime);
        Assert.Equal(source.Goal, runtime.Goal);
        Assert.Equal(source.Steps[1].In["pages"]?.ToJsonString(), runtime.Steps[1].In["pages"]?.ToJsonString());
        Assert.Equal(PlanStepStatuses.Todo, runtime.Steps[0].Status);
        Assert.Null(runtime.Steps[0].Result);
        Assert.Null(runtime.Steps[0].Error);
        Assert.Equal(PlanStepStatuses.Todo, runtime.Steps[1].Status);
        Assert.Null(runtime.Steps[1].Result);
        Assert.Null(runtime.Steps[1].Error);
    }

    [Fact]
    public void PlanValidator_DraftProfile_IgnoresRuntimeStatus_WhileRuntimeProfileRejectsIt()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download search result pages.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    },
                    Status = "broken"
                }
            ]
        };

        var isValidDraft = PlanValidator.TryValidate(
            plan,
            [CreateStaticSearchDescriptor()],
            null,
            PlanModelProfile.Draft,
            out var draftIssue);
        var isValidRuntime = PlanValidator.TryValidate(
            plan,
            [CreateStaticSearchDescriptor()],
            null,
            PlanModelProfile.Runtime,
            out var runtimeIssue);

        Assert.True(isValidDraft, draftIssue?.Message);
        Assert.False(isValidRuntime);
        Assert.Contains("invalid status", runtimeIssue?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanEditingSession_ReadStep_UsesSameActionsForDraftAndRuntimeProfiles()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download search result pages.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement(new { ok = true }),
                    Error = new PlanStepError
                    {
                        Code = "old_error",
                        Message = "old error"
                    }
                }
            ]
        };

        var draftSession = new PlanEditingSession(PlanSanitizer.CloneSanitized(plan, PlanModelProfile.Runtime), PlanModelProfile.Draft);
        var draftResult = draftSession.ExecuteAction("plan.readStep", new JsonObject
        {
            ["stepId"] = "searchPages"
        });
        var draftOutput = draftResult["output"]!.AsObject();

        var runtimeSession = new PlanEditingSession(PlanSanitizer.CloneSanitized(plan, PlanModelProfile.Runtime), PlanModelProfile.Runtime);
        var runtimeResult = runtimeSession.ExecuteAction("plan.readStep", new JsonObject
        {
            ["stepId"] = "searchPages"
        });
        var runtimeOutput = runtimeResult["output"]!.AsObject();

        Assert.True(draftResult["ok"]?.GetValue<bool>() == true);
        Assert.False(draftOutput.ContainsKey("s"));
        Assert.False(draftOutput.ContainsKey("res"));
        Assert.False(draftOutput.ContainsKey("err"));

        Assert.True(runtimeResult["ok"]?.GetValue<bool>() == true);
        Assert.True(runtimeOutput.ContainsKey("s"));
        Assert.True(runtimeOutput.ContainsKey("res"));
        Assert.True(runtimeOutput.ContainsKey("err"));
    }

    [Fact]
    public void PlanEditingSession_RemoveStep_RemovesStepAndResetsDownstreamState()
    {
        var plan = new PlanDefinition
        {
            Goal = "Find robot vacuums.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("robot vacuums")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement(new { ok = true }),
                    Error = new PlanStepError
                    {
                        Code = "old_error",
                        Message = "old error"
                    }
                },
                new PlanStep
                {
                    Id = "deadFormatter",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "gpt-4.1",
                    SystemPrompt = "Format text.",
                    UserPrompt = "Return the provided text unchanged.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["text"] = JsonValue.Create("unused")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.String,
                        Schema = JsonSerializer.SerializeToElement(new { type = "string" })
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement("formatted"),
                    Error = new PlanStepError
                    {
                        Code = "old_error",
                        Message = "old error"
                    }
                },
                new PlanStep
                {
                    Id = "finalAnswer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "gpt-4.1",
                    SystemPrompt = "Summarize.",
                    UserPrompt = "Return the summary.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["text"] = JsonValue.Create("result")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.String,
                        Schema = JsonSerializer.SerializeToElement(new { type = "string" })
                    },
                    Status = PlanStepStatuses.Done,
                    Result = JsonSerializer.SerializeToElement("done"),
                    Error = new PlanStepError
                    {
                        Code = "old_error",
                        Message = "old error"
                    }
                }
            ]
        };

        var session = new PlanEditingSession(plan, PlanModelProfile.Runtime);
        var removeResult = session.ExecuteAction("plan.removeStep", new JsonObject
        {
            ["stepId"] = "deadFormatter"
        });
        var updatedPlan = session.BuildPlan();

        Assert.True(removeResult["ok"]?.GetValue<bool>() == true);
        Assert.Equal(2, updatedPlan.Steps.Count);
        Assert.Equal(["searchPages", "finalAnswer"], updatedPlan.Steps.Select(step => step.Id).ToArray());
        Assert.Equal(1, removeResult["output"]?["resetCount"]?.GetValue<int>());
        Assert.Equal(PlanStepStatuses.Done, updatedPlan.Steps[0].Status);
        Assert.Equal(PlanStepStatuses.Todo, updatedPlan.Steps[1].Status);
        Assert.Null(updatedPlan.Steps[1].Result);
        Assert.Null(updatedPlan.Steps[1].Error);
    }

    [Fact]
    public void PlanValidator_AcceptsSavedAgentStep_WhenCallableAgentExists()
    {
        var callableAgent = CreateCallableAgentDescriptor("character-reader", "Character Reader");
        var plan = new PlanDefinition
        {
            Goal = "Build a character registry.",
            Steps =
            [
                new PlanStep
                {
                    Id = "scanCharacters",
                    Kind = PlanStepKinds.Agent,
                    CapabilityId = callableAgent.Name,
                    UserPrompt = "Read the cursor and update the character registry.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["cursorName"] = JsonValue.Create("book-cursor"),
                        ["registryId"] = JsonValue.Create("characters")
                    },
                    Out = StringOut()
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, tools: null, [callableAgent]);
    }

    [Fact]
    public void PlanValidator_RejectsSavedAgentStep_WhenCallableAgentIsUnknown()
    {
        var plan = new PlanDefinition
        {
            Goal = "Build a character registry.",
            Steps =
            [
                new PlanStep
                {
                    Id = "scanCharacters",
                    Kind = PlanStepKinds.Agent,
                    CapabilityId = "missing-agent",
                    UserPrompt = "Read the cursor and update the character registry.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["cursorName"] = JsonValue.Create("book-cursor")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanValidator.ValidateOrThrow(plan, tools: null, [CreateCallableAgentDescriptor("known-agent", "Known Agent")]));

        Assert.Contains("unknown callable agent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanValidator_RejectsSavedAgentStep_WithSystemPrompt()
    {
        var callableAgent = CreateCallableAgentDescriptor("character-reader", "Character Reader");
        var plan = new PlanDefinition
        {
            Goal = "Build a character registry.",
            Steps =
            [
                new PlanStep
                {
                    Id = "scanCharacters",
                    Kind = PlanStepKinds.Agent,
                    CapabilityId = callableAgent.Name,
                    SystemPrompt = "Do not use this.",
                    UserPrompt = "Read the cursor and update the character registry.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["cursorName"] = JsonValue.Create("book-cursor")
                    },
                    Out = StringOut()
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, tools: null, [callableAgent]));

        Assert.Contains("must not provide systemPrompt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStepRunner_InvokesSavedAgent_FromCallableCatalog()
    {
        var callableAgent = CreateCallableAgentDescriptor("character-reader", "Character Reader");
        var invoker = new Mock<IAgenticExecutionInvoker>();
        invoker
            .Setup(service => service.InvokeAsync(It.IsAny<AgentRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgenticExecutionInvocationResult(
                FinalText: """
                    {"ok":true,"data":{"status":"ok","characterCount":3},"error":null}
                    """,
                IsError: false,
                ErrorMessage: null,
                FunctionCalls: []));

        var runner = new AgentStepRunner(
            Mock.Of<IChatClient>(),
            invoker.Object,
            new PlanningCallableAgentCatalog([callableAgent]));

        var step = new PlanStep
        {
            Id = "scanCharacters",
            Kind = PlanStepKinds.Agent,
            CapabilityId = callableAgent.Name,
            UserPrompt = "Read the cursor and update the character registry.",
            In = new Dictionary<string, JsonNode?>
            {
                ["cursorName"] = JsonValue.Create("book-cursor")
            },
            Out = JsonOut(new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("status", "characterCount"),
                ["properties"] = new JsonObject
                {
                    ["status"] = new JsonObject
                    {
                        ["type"] = "string"
                    },
                    ["characterCount"] = new JsonObject
                    {
                        ["type"] = "integer"
                    }
                }
            })
        };
        var outputContract = DerivedStepOutputContractBuilder.Build(
            new PlanDefinition
            {
                Goal = "Read and summarize characters.",
                Steps = [step]
            },
            tools: null)["scanCharacters"];

        var result = await runner.ExecuteAsync(
            step,
            JsonSerializer.SerializeToElement(new
            {
                cursorName = "book-cursor"
            }),
            outputContract);

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
        Assert.Equal("ok", result.Data.Value.GetProperty("status").GetString());
        Assert.Equal(3, result.Data.Value.GetProperty("characterCount").GetInt32());
        invoker.Verify(service => service.InvokeAsync(
            It.Is<AgentRunRequest>(request =>
                string.Equals(request.Agent.AgentName, "Character Reader", StringComparison.Ordinal)
                && request.UserMessage.Contains("book-cursor", StringComparison.Ordinal)
                && request.UserMessage.Contains("Read the cursor", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void PlanValidator_RejectsToolStepMissingRequiredInput()
    {
        var plan = new PlanDefinition
        {
            Goal = "Search the web.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["limit"] = JsonValue.Create(3)
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));

        Assert.Contains("missing required input 'query'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsLiteralToolInputWithWrongType()
    {
        var plan = new PlanDefinition
        {
            Goal = "Search the web.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = new JsonObject
                        {
                            ["value"] = "maze packages"
                        }
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));

        Assert.Contains("does not match tool schema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Expected string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningSessionService_StartAsync_RequiresEnabledCapabilities()
    {
        var service = CreateSessionService();
        var plannerModel = new ServerModel(Guid.NewGuid(), "model-a");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(new PlanningRunRequest
        {
            Planner = AgentDescriptionFactory.CreateResolved(
                new AgentDescription
                {
                    AgentName = "Planner"
                },
                plannerModel),
            UserQuery = "Compare two products",
        }));

        Assert.Equal("At least one planning tool must be enabled.", exception.Message);
    }

    [Fact]
    public void PlanningSessionService_Reset_ClearsProjectedState()
    {
        var service = CreateSessionService();
        service.State.UserQuery = "compare products";
        service.State.IsRunning = true;
        service.State.IsCompleted = true;
        service.State.ActiveStepId = "answer";
        service.State.CurrentPlan = new PlanDefinition
        {
            Goal = "goal",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "synthesizer",
                    SystemPrompt = "sys",
                    UserPrompt = "user",
                    In = [],
                    Out = StringOut()
                }
            ]
        };
        service.State.FinalResult = ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new { ok = true }));
        service.State.Events.Add(new DiagnosticPlanRunEvent("test", "event"));
        service.State.LogLines.Add("log");
        service.State.AvailableTools.Add(new PlanningToolOption
        {
            Name = SearchToolName,
            DisplayName = "Mock Web: Search",
            Description = "Search the web"
        });

        service.Reset();

        Assert.Equal(string.Empty, service.State.UserQuery);
        Assert.False(service.State.IsRunning);
        Assert.False(service.State.IsCompleted);
        Assert.Null(service.State.ActiveStepId);
        Assert.Null(service.State.CurrentPlan);
        Assert.Null(service.State.FinalResult);
        Assert.Empty(service.State.Events);
        Assert.Empty(service.State.LogLines);
        Assert.Empty(service.State.AvailableTools);
    }

    [Fact]
    public void PlanningJson_SerializesCyrillicWithoutUnicodeEscaping()
    {
        const string text = "\u0420\u0443\u0441\u0441\u043a\u0438\u0439 \u0442\u0435\u043a\u0441\u0442";
        var json = PlanningJson.SerializeIndented(new { text });

        Assert.Contains(text, json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0420", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_ExtractsStructuredResultsFromSearchMarkup()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string html = """
            <html>
              <body>
                <div id="results">
                  <div class="snippet" data-type="web" data-pos="0">
                    <a href="https://example.com/item-a" class="l1">
                      <div class="site-name-content">
                        <div class="desktop-small-semibold">Example</div>
                        <cite class="snippet-url">example.com <span>&gt; item-a</span></cite>
                      </div>
                      <div class="title" title="Item A title">Item A title</div>
                    </a>
                    <div class="generic-snippet">
                      <div class="content"><span class="t-secondary">May 25, 2016 -</span> Item A summary.</div>
                      <a href="https://example.com/item-a" class="thumbnail"><img src="https://example.com/thumb-a.png" /></a>
                    </div>
                  </div>
                </div>
              </body>
            </html>
            """;

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new StubHttpMessageHandler(html)));

        var result = await BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item a"));

        Assert.Equal("item a", result.Query);
        var item = Assert.Single(result.Results);
        Assert.Equal("brave", item.Provider);
        Assert.Equal("https://example.com/item-a", item.Url);
        Assert.Equal("Item A title", item.Title);
        Assert.Equal("Item A summary.", item.Snippet);
        Assert.Equal("Example", item.SiteName);
        Assert.Equal("example.com > item-a", item.DisplayUrl);
        Assert.Equal("May 25, 2016", item.Age);
        Assert.Equal("https://example.com/thumb-a.png", item.ThumbnailUrl);
        Assert.Equal(1, item.Position);
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_DecodesBraveThumbnailProxyUrls()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string originalThumbnailUrl = "https://www.sunfounder.com/cdn/shop/products/Pisloth_1_1024x.jpg?v=1638512159";
        const string braveThumbnailUrl = "https://imgs.search.brave.com/JSVectvQTRixEX4yvuOS250QZINSrqMkmk9El8rp240/rs:fit:200:200:1:0/g:ce/aHR0cHM6Ly93d3cu/c3VuZm91bmRlci5j/b20vY2RuL3Nob3AvcHJvZHVjdHMvUGlz/bG90aF8xXzEwMjR4LmpwZz92PTE2Mzg1MTIxNTk";
        var html = $$"""
            <html>
              <body>
                <div id="results">
                  <div class="snippet" data-type="web" data-pos="0">
                    <a href="https://example.com/item-a" class="l1">
                      <div class="title" title="Item A title">Item A title</div>
                    </a>
                    <div class="generic-snippet">
                      <div class="content">Item A summary.</div>
                      <a href="https://example.com/item-a" class="thumbnail"><img src="{{braveThumbnailUrl}}" /></a>
                    </div>
                  </div>
                </div>
              </body>
            </html>
            """;

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new StubHttpMessageHandler(html)));

        var result = await BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item a"));

        var item = Assert.Single(result.Results);
        Assert.Equal("brave", item.Provider);
        Assert.Equal(originalThumbnailUrl, item.ThumbnailUrl);
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_ReturnsStructuredFailure_WhenStructuredMarkupIsMissing()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string html = """
            <html>
              <body>
                <a href="https://example.com/item-a">Item A</a>
                <a href="https://example.com/item-b">Item B</a>
              </body>
            </html>
            """;

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new StubHttpMessageHandler(html)));

        var exception = await Assert.ThrowsAsync<WebToolException>(() => BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item")));

        Assert.Equal("search_unavailable", exception.Code);
        Assert.Equal("search", exception.Details.Operation);
        Assert.Equal("duckduckgo", exception.Details.Provider);
        Assert.True(exception.Details.FallbackTried);
        Assert.False(exception.Details.NeedsReplan);
        Assert.Equal("error", exception.Details.Type);
        Assert.NotNull(exception.Details.ProviderAttempts);
        Assert.Equal(2, exception.Details.ProviderAttempts!.Count);
        Assert.All(exception.Details.ProviderAttempts, attempt => Assert.Equal("provider_failed", attempt.Outcome));
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_FallsBackToDuckDuckGoHtml_WhenBraveMarkupIsUnavailable()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string braveHtml = """
            <html>
              <body>
                <div>This page needs JavaScript to function.</div>
              </body>
            </html>
            """;

        const string duckHtml = """
            <html>
              <body>
                <div class="result results_links results_links_deep web-result">
                  <div class="links_main links_deep result__body">
                    <h2 class="result__title">
                      <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fitem-a">Item A title</a>
                    </h2>
                    <div class="result__extras">
                      <div class="result__extras__url">
                        <a class="result__url" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fitem-a">example.com/item-a</a>
                      </div>
                    </div>
                    <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fitem-a">Item A summary.</a>
                  </div>
                </div>
              </body>
            </html>
            """;

        var handler = new SequenceHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(braveHtml)
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(duckHtml)
            }
        ]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var result = await BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item a"));

        Assert.Equal(2, handler.CallCount);
        var item = Assert.Single(result.Results);
        Assert.Equal("duckduckgo", item.Provider);
        Assert.Equal("https://example.com/item-a", item.Url);
        Assert.Equal("Item A title", item.Title);
        Assert.Equal("Item A summary.", item.Snippet);
        Assert.Equal("example.com", item.SiteName);
        Assert.Equal("example.com/item-a", item.DisplayUrl);
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_RetriesBraveBeforeFallingBackToDuckDuckGo_WhenBraveIsRateLimited()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string duckHtml = """
            <html>
              <body>
                <div class="result results_links results_links_deep web-result">
                  <div class="links_main links_deep result__body">
                    <h2 class="result__title">
                      <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fitem-a">Item A title</a>
                    </h2>
                    <div class="result__extras">
                      <div class="result__extras__url">
                        <a class="result__url" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fitem-a">example.com/item-a</a>
                      </div>
                    </div>
                    <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fitem-a">Item A summary.</a>
                  </div>
                </div>              
              </body>
            </html>
            """;

        var handler = new SequenceHttpMessageHandler(
        [
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
                return response;
            },
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
                return response;
            },
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
                return response;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(duckHtml)
            }
        ]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var startedAt = Stopwatch.GetTimestamp();
        var result = await BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item a"));
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal(4, handler.CallCount);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(35), $"Search should respect provider retry delays before fallback, actual elapsed={elapsed}.");
        Assert.Single(result.Results);
        Assert.Equal("duckduckgo", result.Results[0].Provider);
        Assert.Equal("https://example.com/item-a", result.Results[0].Url);
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_ReturnsNoResults_WhenProvidersSucceedWithoutNormalizedResults()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string braveHtml = """
            <html>
              <body>
                <div id="results">
                  <div class="snippet" data-type="web" data-pos="0">
                    <a href="https://search.brave.com/result" class="l1">
                      <div class="title" title="Ignored host">Ignored host</div>
                    </a>
                  </div>
                </div>
              </body>
            </html>
            """;

        const string duckHtml = """
            <html>
              <body>
                <div class="result results_links results_links_deep web-result">
                  <div class="links_main links_deep result__body">
                    <h2 class="result__title">
                      <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fsearch.brave.com%2Fresult">Ignored host</a>
                    </h2>
                    <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fsearch.brave.com%2Fresult">Ignored result.</a>
                  </div>
                </div>
              </body>
            </html>
            """;

        var handler = new SequenceHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(braveHtml)
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(duckHtml)
            }
        ]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<WebToolException>(() => BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item")));

        Assert.Equal("search_no_results", exception.Code);
        Assert.Equal("duckduckgo", exception.Details.Provider);
        Assert.True(exception.Details.NeedsReplan);
        Assert.Equal("missing", exception.Details.Type);
        Assert.NotNull(exception.Details.ProviderAttempts);
        Assert.Equal(
            ["success_no_results", "success_no_results"],
            exception.Details.ProviderAttempts!.Select(attempt => attempt.Outcome).ToArray());
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_CachesRepeatedQueriesOnDisk()
    {
        var cacheDirectory = CreateTempSearchCacheDirectory();
        BuiltInWebToolLogic.ResetSearchStateForTests(cacheDirectory);

        const string html = """
            <html>
              <body>
                <div id="results">
                  <div class="snippet" data-type="web" data-pos="0">
                    <a href="https://example.com/item-a" class="l1">
                      <div class="title" title="Item A title">Item A title</div>
                    </a>
                    <div class="generic-snippet">
                      <div class="content">Item A summary.</div>
                    </div>
                  </div>
                </div>
              </body>
            </html>
            """;

        var handler = new SequenceHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            },
            _ => throw new InvalidOperationException("The second identical query should have been served from the in-memory cache.")
        ]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var first = await BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item a"));
        BuiltInWebToolLogic.ResetSearchStateForTests(cacheDirectory);
        var second = await BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item a"));

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(first.Results[0].Url, second.Results[0].Url);
        Assert.Single(Directory.GetFiles(cacheDirectory, "*.json"));
    }

    [Fact]
    public async Task BuiltInWebDownloadLogic_PreservesPageObjectAndAddsContent()
    {
        const string html = """
            <html>
              <head><title>Downloaded page title</title></head>
              <body>
                <main><p>Example body text.</p></main>
              </body>
            </html>
            """;

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new StubHttpMessageHandler(html)));

        var result = await BuiltInWebToolLogic.DownloadAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebDownloadInput(new WebDownloadPageRef(
                Url: "https://example.com/item-a",
                Title: "Search title",
                Snippet: "Search snippet",
                SiteName: "Example",
                DisplayUrl: null,
                Age: null,
                ThumbnailUrl: null,
                Position: null)));

        Assert.Equal("https://example.com/item-a", result.Url);
        Assert.Equal("Search title", result.Title);
        Assert.Equal("Search snippet", result.Snippet);
        Assert.Equal("Example", result.SiteName);
        Assert.Equal("Downloaded page title Example body text.", result.Content);
    }

    [Fact]
    public async Task BuiltInWebDownloadLogic_ThrowsStructuredHttpError_WhenRemoteSiteRejectsRequest()
    {
        var handler = new SequenceHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        ]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<WebToolException>(() => BuiltInWebToolLogic.DownloadAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebDownloadInput(Url: "https://example.com/protected")));

        Assert.Equal("download_http_error", exception.Code);
        Assert.Equal("download", exception.Details.Operation);
        Assert.Equal("example.com", exception.Details.Host);
        Assert.Equal(403, exception.Details.HttpStatusCode);
        Assert.False(exception.Details.Retryable);
        Assert.True(exception.Details.NeedsReplan);
        Assert.Contains(exception.Details.Details, detail => string.Equals(detail, "httpStatusCode=403", StringComparison.Ordinal));
        Assert.Contains(exception.Details.Details, detail => string.Equals(detail, "retryable=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuiltInWebMcpServerTools_DownloadAsync_ReturnsStructuredErrorResult_WhenDownloadFails()
    {
        var handler = new SequenceHttpMessageHandler(
        [
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        ]);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var result = await BuiltInWebMcpServerTools.DownloadAsync(
            httpClientFactory.Object,
            NullLogger<BuiltInWebMcpServerTools>.Instance,
            url: "https://example.com/protected");

        var toolResult = Assert.IsType<CallToolResult>(result);
        Assert.True(toolResult.IsError);
        var structured = Assert.IsType<JsonObject>(toolResult.StructuredContent);
        Assert.Equal("download_http_error", structured["code"]?.GetValue<string>());
        Assert.Equal("example.com", structured["host"]?.GetValue<string>());
        Assert.Equal(false, structured["retryable"]?.GetValue<bool>());
        Assert.Equal(403, structured["httpStatusCode"]?.GetValue<int>());
        Assert.Equal(true, structured["needsReplan"]?.GetValue<bool>());
    }

    [Fact]
    public async Task BuiltInWebMcpServerTools_SearchAsync_ReturnsStructuredErrorResult_WhenSearchFails()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

        const string html = """
            <html>
              <body>
                <a href="https://example.com/item-a">Item A</a>
              </body>
            </html>
            """;

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new StubHttpMessageHandler(html)));

        var result = await BuiltInWebMcpServerTools.SearchAsync(
            httpClientFactory.Object,
            NullLogger<BuiltInWebMcpServerTools>.Instance,
            query: "item");

        var toolResult = Assert.IsType<CallToolResult>(result);
        Assert.True(toolResult.IsError);
        var structured = Assert.IsType<JsonObject>(toolResult.StructuredContent);
        Assert.Equal("search_unavailable", structured["code"]?.GetValue<string>());
        Assert.Equal("duckduckgo", structured["provider"]?.GetValue<string>());
        Assert.Equal("item", structured["query"]?.GetValue<string>());
        Assert.Equal(true, structured["fallbackTried"]?.GetValue<bool>());
        Assert.Equal(false, structured["needsReplan"]?.GetValue<bool>());
        Assert.Equal("error", structured["type"]?.GetValue<string>());
        var providerAttempts = Assert.IsType<JsonArray>(structured["providerAttempts"]);
        Assert.Equal(2, providerAttempts.Count);
    }

    [Fact]
    public async Task PlanExecutor_MapsProjectedUrlField_WhenBindingUsesMapMode()
    {
        var downloadTool = CreateDownloadByUrlDescriptor();
        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), downloadTool.Descriptor]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Download search results.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = Ref("$searchPages.results[].url", mode: "map")
                    }
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.Equal(StepTraceOutcome.Done, trace.Outcome));
        Assert.Equal(
            ["https://example.com/item-a", "https://example.com/item-b"],
            downloadTool.ReceivedUrls);
    }

    [Fact]
    public async Task PlanExecutor_MapsWholeSearchObjects_WhenBindingUsesMapMode()
    {
        var downloadTool = CreateDownloadByPageDescriptor();
        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), downloadTool.Descriptor]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Download full search result objects.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = Ref("$searchPages.results", mode: "map")
                    }
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.Equal(StepTraceOutcome.Done, trace.Outcome));
        Assert.Equal(["Item A", "Item B"], downloadTool.ReceivedTitles);
    }

    [Fact]
    public void PlanValidator_AcceptsPageBinding_WhenToolSchemaUsesOneOfAndPageRequiresUrlOnly()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download full search result objects.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = Ref("$searchPages.results", mode: "map")
                    }
                }
            ]
        };

        PlanValidator.ValidateOrThrow(
            plan,
            [CreateStaticSearchDescriptor(), CreateStrictDownloadDescriptor()]);
    }

    [Fact]
    public void PlanValidator_RejectsDownloadInput_WhenOneOfMatchesMultipleAlternatives()
    {
        var plan = new PlanDefinition
        {
            Goal = "Download search result.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = Ref("$searchPages.results[0]"),
                        ["url"] = JsonValue.Create("https://example.com/item-a")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(
            plan,
            [CreateStaticSearchDescriptor(), CreateStrictDownloadDescriptor()]));

        Assert.Contains("multiple oneOf schema alternatives", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_RejectsLegacyStringRefsInsideInputs()
    {
        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), CreateDownloadByUrlDescriptor().Descriptor]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Download search results.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("$searchPages.results[].url")
                    }
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan));

        Assert.Contains("uses legacy string ref syntax", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_Throws_WhenMapBindingResolvesToNonArray()
    {
        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), CreateDownloadByUrlDescriptor().Descriptor]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Download search result pages.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = Ref("$searchPages.query", mode: "map")
                    }
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan));

        Assert.Contains("uses mode='map' but ref '$searchPages.query' did not resolve to an array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsBindingWhoseSourceSchemaIsIncompatibleWithTargetToolInput()
    {
        var plan = new PlanDefinition
        {
            Goal = "Reuse a search result object as a search query.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "searchAgain",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = Ref("$searchPages.results[0]")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));

        Assert.Contains("bound source schema produces object", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expects string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_AllowsLlmBindingType_WhenValueModeMatchesArraySource()
    {
        var plan = new PlanDefinition
        {
            Goal = "Shortlist packages from search results.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "shortlistPackages",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "shortlist",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Choose three packages.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["results"] = Ref("$searchPages.results", type: "array<object>"),
                        ["topic"] = JsonValue.Create("maze generator")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("name"),
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        }
                    })
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]);
    }

    [Fact]
    public void PlanValidator_AllowsLlmBindingType_WhenMapModeMatchesItemSource()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract one object per result.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "extractPackage",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract one package.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["result"] = Ref("$searchPages.results", mode: "map", type: "object")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    }, aggregate: "collect")
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]);
    }

    [Fact]
    public void PlanValidator_RejectsLlmBindingType_WhenMapModeClaimsArray()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract one array per mapped result.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "extractPackage",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract one package.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["result"] = Ref("$searchPages.results", mode: "map", type: "array<object>")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    }, aggregate: "collect")
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));

        Assert.Contains("declares type 'array<object>'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("produces object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_AcceptsMappedLlmStep_WhenDownstreamBindsFlatArray()
    {
        var plan = new PlanDefinition
        {
            Goal = "Collect mapped arrays, then bind them as a flat array.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "extractPackages",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract package candidates from the current page.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["result"] = Ref("$searchPages.results", mode: "map", type: "object")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("name"),
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        }
                    }, aggregate: "collect")
                },
                new PlanStep
                {
                    Id = "reviewPackages",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "review",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Review the extracted packages.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["packages"] = Ref("$extractPackages", type: "array<object>")
                    },
                    Out = StringOut()
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]);
    }

    [Fact]
    public void PlanValidator_RejectsLlmBindingType_WhenValueModeClaimsObjectForArraySource()
    {
        var plan = new PlanDefinition
        {
            Goal = "Shortlist packages from search results.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "shortlistPackages",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "shortlist",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Choose three packages.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["results"] = Ref("$searchPages.results", type: "object")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("name"),
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        }
                    })
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));

        Assert.Contains("declares type 'object'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("produces array", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_Fails_WhenResolvedToolInputViolatesInputSchema_AtRuntime()
    {
        var opaqueTool = CreateOpaqueProducerDescriptor();
        var executor = new PlanExecutor(
            new PlanningToolCatalog([opaqueTool, CreateStaticSearchDescriptor()]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Feed opaque output into a search tool.",
            Steps =
            [
                new PlanStep
                {
                    Id = "opaqueSource",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock-web:opaque",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["seed"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "searchAgain",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = Ref("$opaqueSource.payload")
                    }
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, [opaqueTool, CreateStaticSearchDescriptor()]);

        var result = await executor.ExecuteAsync(plan);
        var trace = Assert.Single(result.StepTraces, trace => trace.Outcome == StepTraceOutcome.Failed);
        Assert.Equal("searchAgain", trace.StepId);
        Assert.Equal("input_contract_failed", trace.ErrorCode);
        Assert.Contains("does not match its input schema", trace.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_Throws_WhenMappedInputsHaveDifferentLengths()
    {
        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateVariableSearchDescriptor(), CreatePairDescriptor()]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Zip two mapped inputs.",
            Steps =
            [
                new PlanStep
                {
                    Id = "manyResults",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("many")
                    }
                },
                new PlanStep
                {
                    Id = "singleResult",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("single")
                    }
                },
                new PlanStep
                {
                    Id = "pairResults",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = PairToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["first"] = Ref("$manyResults.results[].url", mode: "map"),
                        ["second"] = Ref("$singleResult.results[].url", mode: "map")
                    }
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan));

        Assert.Contains("resolves multiple array inputs with different lengths", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'first' (2) and 'second' (1)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_AllowsJsonLlmStepWithoutOutputSchema()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract a product summary.",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract the requested fields.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["content"] = JsonValue.Create("example")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.Json
                    }
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan);
    }

    [Fact]
    public void PlanValidator_AllowsMappedLlmStepWithoutAuthoredAggregate()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract package facts.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract one package per page.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = Ref("$searchPages.results", mode: "map")
                    },
                    Out = JsonOut(
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["required"] = new JsonArray("name"),
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject
                                {
                                    ["type"] = "string"
                                }
                            }
                        },
                        aggregate: "single")
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan);
    }

    [Fact]
    public void DerivedStepOutputContractBuilder_UsesToolSchema_ForMappedArrayTool()
    {
        var collectArrayTool = CreateDescriptor(
            serverName: "mock-web",
            toolName: "collect-array",
            description: "Return multiple values per input.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string" }
                  },
                  "required": ["item"]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "value": { "type": "string" }
                    },
                    "required": ["value"]
                  }
                }
                """,
            execute: _ => new[]
            {
                new { value = "a" }
            });
        var plan = new PlanDefinition
        {
            Goal = "Collect mapped items.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "collectItems",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock-web:collect-array",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["item"] = Ref("$searchPages.results[].title", "map")
                    }
                }
            ]
        };

        var contract = DerivedStepOutputContractBuilder.Build(plan, [CreateStaticSearchDescriptor(), collectArrayTool])["collectItems"];

        Assert.True(contract.IsMapped);
        Assert.Equal(DerivedStepOutputContractSource.ToolOutputSchema, contract.Source);
        Assert.True(contract.CallSchema.HasValue);
        Assert.True(PlanStepOutputContractResolver.SchemaDefinesArray(contract.CallSchema.Value));
        Assert.True(contract.FinalSchema.HasValue);
        Assert.True(PlanStepOutputContractResolver.SchemaDefinesArray(contract.FinalSchema.Value));
    }

    [Fact]
    public void PlanValidator_AcceptsNullableJsonPropertySchemas()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract optional fields.",
            Steps =
            [
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract the product facts.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["content"] = JsonValue.Create("example")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name", "noiseLevel"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string"
                            },
                            ["noiseLevel"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["nullable"] = true
                            }
                        }
                    })
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan);
    }

    [Fact]
    public async Task PlanExecutor_FlattenAggregate_FlattensMappedLlmArrayOutputs()
    {
        var runner = new DelegateAgentStepRunner((step, resolvedInputs) =>
        {
            var page = resolvedInputs.GetProperty("page");
            var title = page.GetProperty("title").GetString();
            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new[]
            {
                new { name = $"{title} A" },
                new { name = $"{title} B" }
            }));
        });

        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), CreateDownloadByPageDescriptor().Descriptor]),
            runner);

        var plan = new PlanDefinition
        {
            Goal = "Extract multiple packages per page and flatten them.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract all package names from the page.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = Ref("$searchPages.results", mode: "map")
                    },
                    Out = JsonOut(
                        new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["required"] = new JsonArray("name"),
                                ["properties"] = new JsonObject
                                {
                                    ["name"] = new JsonObject
                                    {
                                        ["type"] = "string"
                                    }
                                }
                            }
                        },
                        aggregate: "flatten")
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.Equal(StepTraceOutcome.Done, trace.Outcome));
        var extractStep = Assert.Single(plan.Steps, step => step.Id == "extractFacts");
        var items = extractStep.Result!.Value.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Assert.Collection(
            items,
            item => Assert.Equal("Item A A", item),
            item => Assert.Equal("Item A B", item),
            item => Assert.Equal("Item B A", item),
            item => Assert.Equal("Item B B", item));
    }

    [Fact]
    public async Task PlanExecutor_Fails_WhenLlmOutputViolatesDeclaredSchema()
    {
        var runner = new DelegateAgentStepRunner((step, resolvedInputs) =>
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new
            {
                description = "missing name"
            })));

        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor()]),
            runner);

        var plan = new PlanDefinition
        {
            Goal = "Return one extracted object.",
            Steps =
            [
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract the package.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["content"] = JsonValue.Create("example")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    })
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);
        var trace = Assert.Single(result.StepTraces);
        Assert.Equal(StepTraceOutcome.Failed, trace.Outcome);
        Assert.Equal("output_contract_failed", trace.ErrorCode);
        Assert.Contains("derived output contract", trace.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("call output", trace.ErrorDetails?.GetProperty("issues")[0].GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_Fails_WhenResolvedLlmInputViolatesDeclaredBindingType_AtRuntime()
    {
        var opaqueTool = CreateOpaqueProducerDescriptor();
        var executor = new PlanExecutor(
            new PlanningToolCatalog([opaqueTool]),
            new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Validate LLM input shape at runtime.",
            Steps =
            [
                new PlanStep
                {
                    Id = "opaqueSource",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock-web:opaque",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["seed"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract package facts.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["results"] = Ref("$opaqueSource.payload", type: "array<object>")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    })
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, [opaqueTool]);

        var result = await executor.ExecuteAsync(plan);
        var trace = Assert.Single(result.StepTraces, candidate => candidate.Outcome == StepTraceOutcome.Failed);
        Assert.Equal("extractFacts", trace.StepId);
        Assert.Equal("llm_input_contract_failed", trace.ErrorCode);
        Assert.Contains("declared input type hints", trace.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("expected 'array<object>'", trace.ErrorDetails?.GetProperty("issues")[0].GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentStepRunner_BuildExecutionContract_ClarifiesMappedItemSemantics()
    {
        var callSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new
                {
                    type = "string"
                }
            }
        });
        var finalSchema = JsonSerializer.SerializeToElement(new
        {
            type = "array",
            items = new
            {
                type = "object",
                properties = new
                {
                    name = new
                    {
                        type = "string"
                    }
                }
            }
        });

        var prompt = AgentStepRunner.BuildExecutionContract(new ResolvedPlanStepOutputContract(
            Format: PlanStepOutputFormats.Json,
            IsMapped: true,
            CallSchema: callSchema,
            FinalSchema: finalSchema,
            Source: DerivedStepOutputContractSource.ExplicitOutputSchema,
            IsOpaque: false));

        Assert.Contains("return one logical item or an array of logical items", prompt, StringComparison.Ordinal);
        Assert.Contains("flat final array of logical items", prompt, StringComparison.Ordinal);
        Assert.Contains("schema below describes one logical item", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanExecutor_AllowsNullField_WhenSchemaUsesTypeUnionWithNull()
    {
        var runner = new DelegateAgentStepRunner((step, resolvedInputs) =>
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new
            {
                name = "RoboClean A1 Max",
                noiseLevel = (string?)null
            })));

        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor()]),
            runner);

        var plan = new PlanDefinition
        {
            Goal = "Extract one object with an optional field.",
            Steps =
            [
                new PlanStep
                {
                    Id = "extractFacts",
                    Kind = PlanStepKinds.Llm,
                    CapabilityId = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract the package.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["content"] = JsonValue.Create("example")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name", "noiseLevel"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string"
                            },
                            ["noiseLevel"] = new JsonObject
                            {
                                ["type"] = new JsonArray("string", "null")
                            }
                        }
                    })
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);
        var trace = Assert.Single(result.StepTraces);
        Assert.Equal(StepTraceOutcome.Done, trace.Outcome);
        Assert.Null(trace.ErrorCode);
    }

    [Fact]
    public async Task PlanExecutor_ContinuesMappedStep_WhenSomeCallsFailAndPartialDataExists()
    {
        var collectDescriptor = CreateDescriptor(
            serverName: "mock-web",
            toolName: "collect",
            description: "Collect one mapped item.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string" }
                  },
                  "required": ["item"]
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
            execute: arguments =>
            {
                var item = GetRequiredString(arguments, "item");
                if (string.Equals(item, "item-b", StringComparison.Ordinal))
                    throw new InvalidOperationException("synthetic mapped failure");

                return new
                {
                    value = item.ToUpperInvariant()
                };
            });

        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), collectDescriptor]),
            new ThrowingAgentStepRunner());

        var plan = new PlanDefinition
        {
            Goal = "Collect mapped items even when one call fails.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchItems",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "collectItems",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock-web:collect",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["item"] = Ref("$searchItems.results[].title", "map")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("value"),
                        ["properties"] = new JsonObject
                        {
                            ["value"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    }, aggregate: "collect")
                }
            ]
        };

        plan.Steps[0].Result = JsonSerializer.SerializeToElement(new
        {
            query = "example",
            results = new[]
            {
                new { url = "https://example.com/a", title = "item-a" },
                new { url = "https://example.com/b", title = "item-b" },
                new { url = "https://example.com/c", title = "item-c" }
            }
        });
        plan.Steps[0].Status = PlanStepStatuses.Done;

        var result = await executor.ExecuteAsync(plan);

        Assert.False(result.HasErrors);
        var collectStep = plan.Steps[1];
        Assert.Equal(PlanStepStatuses.Partial, collectStep.Status);
        Assert.Equal("partial_failure", collectStep.Error?.Code);
        Assert.NotNull(collectStep.Result);
        Assert.Equal(JsonValueKind.Array, collectStep.Result?.ValueKind);
        Assert.Equal(2, collectStep.Result?.GetArrayLength());

        var collectTrace = result.StepTraces.Single(trace => trace.StepId == "collectItems");
        Assert.Equal(StepTraceOutcome.Partial, collectTrace.Outcome);
        Assert.Equal("partial_failure", collectTrace.ErrorCode);
    }

    [Fact]
    public async Task PlanExecutor_FailsMappedStep_WhenAllMappedCallsFail()
    {
        var failingCollectDescriptor = CreateDescriptor(
            serverName: "mock-web",
            toolName: "collect",
            description: "Always fail mapped item collection.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string" }
                  },
                  "required": ["item"]
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
            execute: _ => throw new InvalidOperationException("synthetic mapped failure"));

        var executor = new PlanExecutor(
            new PlanningToolCatalog([CreateStaticSearchDescriptor(), failingCollectDescriptor]),
            new ThrowingAgentStepRunner());

        var plan = new PlanDefinition
        {
            Goal = "Fail when no mapped call succeeds.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchItems",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "collectItems",
                    Kind = PlanStepKinds.Tool,
                    CapabilityId = "mock-web:collect",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["item"] = Ref("$searchItems.results[].title", "map")
                    },
                    Out = JsonOut(new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("value"),
                        ["properties"] = new JsonObject
                        {
                            ["value"] = new JsonObject
                            {
                                ["type"] = "string"
                            }
                        }
                    }, aggregate: "collect")
                }
            ]
        };

        plan.Steps[0].Result = JsonSerializer.SerializeToElement(new
        {
            query = "example",
            results = new[]
            {
                new { url = "https://example.com/a", title = "item-a" }
            }
        });
        plan.Steps[0].Status = PlanStepStatuses.Done;

        var result = await executor.ExecuteAsync(plan);

        Assert.True(result.HasErrors);
        var collectStep = plan.Steps[1];
        Assert.Equal(PlanStepStatuses.Fail, collectStep.Status);
        Assert.Equal("tool_error", collectStep.Error?.Code);
        Assert.Null(collectStep.Result);
    }

    private static PlanningSessionService CreateSessionService()
    {
        var chatClientFactory = new Mock<ILlmChatClientFactory>();
        var appToolCatalog = new Mock<IAppToolCatalog>();
        var mcpUserInteractionService = new Mock<IMcpUserInteractionService>();
        var agenticExecutionInvoker = new Mock<IAgenticExecutionInvoker>();
        appToolCatalog
            .Setup(catalog => catalog.ListToolsAsync(It.IsAny<McpClientRequestContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AppToolDescriptor>());

        return new PlanningSessionService(
            chatClientFactory.Object,
            appToolCatalog.Object,
            mcpUserInteractionService.Object,
            agenticExecutionInvoker.Object,
            NullLogger<PlanningSessionService>.Instance);
    }

    private static PlanningCallableAgentDescriptor CreateCallableAgentDescriptor(string name, string displayName)
    {
        var serverId = Guid.NewGuid();
        var agent = new AgentDescription
        {
            Id = Guid.NewGuid(),
            AgentName = displayName,
            ShortName = name,
            Content = $"Agent {displayName} can work on long-running cursor tasks.",
            ModelName = "model-a",
            LlmId = serverId
        };

        return new PlanningCallableAgentDescriptor
        {
            Name = name,
            DisplayName = displayName,
            Description = $"Callable agent {displayName}",
            Agent = AgentDescriptionFactory.CreateResolved(agent, new ServerModel(serverId, "model-a"))
        };
    }

    private static AppToolDescriptor CreateStaticSearchDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "search",
            description: "Structured search results.",
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
            execute: _ => new
            {
                query = "example",
                results = new[]
                {
                    new { url = "https://example.com/item-a", title = "Item A" },
                    new { url = "https://example.com/item-b", title = "Item B" }
                }
            });

    private static AppToolDescriptor CreateVariableSearchDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "search",
            description: "Structured search results with variable lengths.",
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
            execute: arguments =>
            {
                var query = GetRequiredString(arguments, "query");
                var results = string.Equals(query, "single", StringComparison.Ordinal)
                    ? new[]
                    {
                        new { url = "https://example.com/item-only", title = "Item Only" }
                    }
                    : new[]
                    {
                        new { url = "https://example.com/item-a", title = "Item A" },
                        new { url = "https://example.com/item-b", title = "Item B" }
                    };

                return new { query, results };
            });

    private static AppToolDescriptor CreatePairDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "pair",
            description: "Pair two scalar values.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "first": { "type": "string" },
                    "second": { "type": "string" }
                  },
                  "required": ["first", "second"]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "first": { "type": "string" },
                    "second": { "type": "string" }
                  },
                  "required": ["first", "second"]
                }
                """,
            execute: arguments => new
            {
                first = GetRequiredString(arguments, "first"),
                second = GetRequiredString(arguments, "second")
            });

    private static AppToolDescriptor CreateOpaqueProducerDescriptor() =>
        new(
            QualifiedName: "mock-web:opaque",
            ServerName: "mock-web",
            ToolName: "opaque",
            DisplayName: "opaque",
            Description: "Produce opaque output without an output schema.",
            InputSchema: ParseJsonElement("""
                {
                  "type": "object",
                  "properties": {
                    "seed": { "type": "string" }
                  },
                  "required": ["seed"]
                }
                """),
            OutputSchema: null,
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: (arguments, _) => Task.FromResult<object>(new
            {
                payload = new
                {
                    unexpected = GetRequiredString(arguments, "seed")
                }
            }));

    private static RecordingDownloadDescriptor CreateDownloadByUrlDescriptor()
    {
        var receivedUrls = new List<string>();
        var descriptor = CreateDescriptor(
            serverName: "mock-web",
            toolName: "download",
            description: "Download by URL.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" }
                  },
                  "required": ["url"]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" }
                  },
                  "required": ["url"]
                }
                """,
            execute: arguments =>
            {
                var url = GetRequiredString(arguments, "url");
                receivedUrls.Add(url);
                return new { url };
            });

        return new RecordingDownloadDescriptor(descriptor, receivedUrls);
    }

    private static PageRecordingDownloadDescriptor CreateDownloadByPageDescriptor()
    {
        var receivedTitles = new List<string>();
        var descriptor = CreateDescriptor(
            serverName: "mock-web",
            toolName: "download",
            description: "Download by page object.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "page": { "type": "object" }
                  }
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" },
                    "title": { "type": "string" },
                    "content": { "type": "string" }
                  },
                  "required": ["url", "title", "content"]
                }
                """,
            execute: arguments =>
            {
                var page = GetRequiredObject(arguments, "page");
                var title = GetRequiredString(page, "title");
                receivedTitles.Add(title);
                return new
                {
                    url = GetRequiredString(page, "url"),
                    title,
                    content = "content"
                };
            });

        return new PageRecordingDownloadDescriptor(descriptor, receivedTitles);
    }

    private static AppToolDescriptor CreateStrictDownloadDescriptor() =>
        CreateDescriptor(
            serverName: "mock-web",
            toolName: "download",
            description: "Download by page reference or URL.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "page": {
                      "type": "object",
                      "properties": {
                        "url": { "type": "string" },
                        "title": { "type": ["string", "null"] }
                      },
                      "required": ["url"]
                    },
                    "url": { "type": "string" }
                  },
                  "oneOf": [
                    { "required": ["page"] },
                    { "required": ["url"] }
                  ]
                }
                """,
            outputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" },
                    "title": { "type": "string" },
                    "content": { "type": "string" }
                  },
                  "required": ["url", "title", "content"]
                }
                """,
            execute: arguments => new
            {
                url = GetRequiredString(arguments, "url"),
                title = "Example title",
                content = "content"
            });

    private static AppToolDescriptor CreateDescriptor(
        string serverName,
        string toolName,
        string description,
        string inputSchemaJson,
        string outputSchemaJson,
        Func<Dictionary<string, object?>, object> execute) =>
        new(
            QualifiedName: $"{serverName}:{toolName}",
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: description,
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

    private static Dictionary<string, object?> GetRequiredObject(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is not Dictionary<string, object?> result)
            throw new InvalidOperationException($"Expected argument '{key}' to be an object.");

        return result;
    }

    private static JsonNode? Ref(string value, string mode = "value", string? type = null)
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

    private static PlanStepOutputContract JsonOut(JsonObject schema, string? aggregate = null) =>
        new()
        {
            Format = PlanStepOutputFormats.Json,
            Schema = JsonSerializer.SerializeToElement(schema)
        };

    private static PlanStepOutputContract StringOut(string? aggregate = null) =>
        new()
        {
            Format = PlanStepOutputFormats.String
        };

    private static string CreateTempSearchCacheDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ollamachat-web-search-cache-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record RecordingDownloadDescriptor(AppToolDescriptor Descriptor, List<string> ReceivedUrls);

    private sealed record PageRecordingDownloadDescriptor(AppToolDescriptor Descriptor, List<string> ReceivedTitles);

    private sealed class StubHttpMessageHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            });
    }

    private sealed class SequenceHttpMessageHandler(IReadOnlyList<Func<HttpRequestMessage, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = _callCount++;
            var factory = index < responses.Count ? responses[index] : responses[^1];
            return Task.FromResult(factory(request));
        }
    }

    private sealed class ThrowingAgentStepRunner : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, ResolvedPlanStepOutputContract outputContract, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("LLM execution is not expected in this test.");
    }

    private sealed class DelegateAgentStepRunner(Func<PlanStep, JsonElement, ResultEnvelope<JsonElement?>> execute) : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, ResolvedPlanStepOutputContract outputContract, CancellationToken cancellationToken = default) =>
            Task.FromResult(execute(step, resolvedInputs));
    }
}




