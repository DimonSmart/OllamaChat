using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ChatClient.Tests;

public sealed class RuntimeExecutorDiagnosticsTests
{
    [Fact]
    public async Task RuntimePlanExecutor_ReportsPerItemToolFailures_AsStructuredDiagnostics()
    {
        var events = new List<PlanRunEvent>();
        var executor = new RuntimePlanExecutor(
            new ThrowingPlanningLlmClient(),
            [CreateSeedPagesTool(), CreatePartiallyFailingDownloadTool()],
            planRunObserver: new ActionPlanRunObserver(events.Add));

        var result = await executor.ExecuteAsync(CreateMappedDownloadRuntimePlan());

        Assert.True(result.Succeeded);
        Assert.Equal("executed", result.Status);

        var documents = Assert.IsType<JsonArray>(result.FinalOutput);
        Assert.Equal(2, documents.Count);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("mapped_tool_item_failed", issue.Code);
        Assert.False(issue.IsBlocking);
        Assert.NotNull(issue.Details);
        Assert.Equal("download_pages", issue.Details!.Value.GetProperty("stepId").GetString());
        Assert.Equal(1, issue.Details!.Value.GetProperty("itemIndex").GetInt32());
        Assert.Equal(
            "https://example.com/b",
            issue.Details!.Value.GetProperty("input").GetProperty("page").GetProperty("url").GetString());
        Assert.Equal(
            "download_http_error",
            issue.Details!.Value.GetProperty("error").GetProperty("details").GetProperty("code").GetString());

        var callEvents = events
            .OfType<StepCallCompletedEvent>()
            .Where(evt => string.Equals(evt.StepId, "download_pages", StringComparison.Ordinal))
            .OrderBy(evt => evt.CallIndex)
            .ToList();

        Assert.Equal(3, callEvents.Count);
        Assert.True(callEvents[0].Ok);
        Assert.False(callEvents[1].Ok);
        Assert.True(callEvents[2].Ok);
        Assert.Equal(
            "download_http_error",
            callEvents[1].Error!.Details!.Value.GetProperty("code").GetString());

        var stepCompleted = Assert.Single(
            events.OfType<RuntimeStepCompletedEvent>(),
            evt => string.Equals(evt.StepId, "download_pages", StringComparison.Ordinal));
        Assert.True(stepCompleted.Ok);
        Assert.NotNull(stepCompleted.Output);

        var diagnostics = stepCompleted.Output!.Value.GetProperty("__diagnostics");
        Assert.Equal(3, diagnostics.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, diagnostics.GetProperty("successCount").GetInt32());
        Assert.Equal(1, diagnostics.GetProperty("failureCount").GetInt32());
        Assert.False(diagnostics.GetProperty("anyNeedsReplan").GetBoolean());
        Assert.False(diagnostics.GetProperty("allFailuresRetryable").GetBoolean());
        Assert.Equal("https://example.com/b", Assert.Single(diagnostics.GetProperty("failedUrls").EnumerateArray()).GetString());

        var failedCall = diagnostics.GetProperty("calls")
            .EnumerateArray()
            .Single(call => !call.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "https://example.com/b",
            failedCall.GetProperty("input").GetProperty("page").GetProperty("url").GetString());
        Assert.Equal(
            "download_http_error",
            failedCall.GetProperty("error").GetProperty("details").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RuntimePlanExecutor_FailsWhenEveryMappedToolCallFails_AndCarriesAggregateDiagnostics()
    {
        var events = new List<PlanRunEvent>();
        var executor = new RuntimePlanExecutor(
            new ThrowingPlanningLlmClient(),
            [CreateSeedPagesTool(), CreateAlwaysFailingDownloadTool()],
            planRunObserver: new ActionPlanRunObserver(events.Add));

        var result = await executor.ExecuteAsync(CreateMappedDownloadRuntimePlan());

        Assert.False(result.Succeeded);
        Assert.Equal("execution_failed", result.Status);

        var aggregateIssue = Assert.Single(result.Issues, issue => issue.IsBlocking);
        Assert.Equal("mapped_tool_all_items_failed", aggregateIssue.Code);
        Assert.NotNull(aggregateIssue.Details);
        Assert.Equal(3, aggregateIssue.Details!.Value.GetProperty("totalItems").GetInt32());
        Assert.Equal(3, aggregateIssue.Details!.Value.GetProperty("failureCount").GetInt32());
        Assert.False(aggregateIssue.Details!.Value.GetProperty("anyNeedsReplan").GetBoolean());
        Assert.False(aggregateIssue.Details!.Value.GetProperty("allFailuresRetryable").GetBoolean());
        Assert.Equal(3, aggregateIssue.Details!.Value.GetProperty("failedUrls").GetArrayLength());

        var stepCompleted = Assert.Single(
            events.OfType<RuntimeStepCompletedEvent>(),
            evt => string.Equals(evt.StepId, "download_pages", StringComparison.Ordinal));
        Assert.False(stepCompleted.Ok);
        Assert.NotNull(stepCompleted.Error);
        Assert.NotNull(stepCompleted.Error!.Details);
        Assert.Equal(3, stepCompleted.Error!.Details!.Value.GetProperty("calls").GetArrayLength());

        var callEvents = events
            .OfType<StepCallCompletedEvent>()
            .Where(evt => string.Equals(evt.StepId, "download_pages", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, callEvents.Count);
        Assert.All(callEvents, callEvent => Assert.False(callEvent.Ok));
    }

    [Fact]
    public async Task RuntimePlanExecutor_DoesNotPoisonSingleLlmStepAfterPartialMappedToolFailure_AndPassesDiagnostics()
    {
        var llmClient = new CapturingPlanningLlmClient(_ => ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement("Best-effort summary")));
        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateSeedPagesTool(), CreatePartiallyFailingDownloadTool()]);

        var result = await executor.ExecuteAsync(CreateMappedDownloadThenSummarizeRuntimePlan());

        Assert.True(result.Succeeded);
        Assert.Equal("executed", result.Status);
        Assert.Equal("Best-effort summary", Assert.IsAssignableFrom<JsonValue>(result.FinalOutput).GetValue<string>());

        var issue = Assert.Single(result.Issues);
        Assert.Equal("mapped_tool_item_failed", issue.Code);
        Assert.False(issue.IsBlocking);

        var llmInputs = Assert.Single(llmClient.CapturedInputs);
        var inputDiagnostics = Assert.IsType<JsonObject>(llmInputs["_inputDiagnostics"]);
        Assert.NotNull(inputDiagnostics["documents"]);
        var documentsDiagnostics = Assert.IsType<JsonObject>(inputDiagnostics["documents"]!);
        Assert.Equal(3, documentsDiagnostics["totalItems"]!.GetValue<int>());
        Assert.Equal(2, documentsDiagnostics["successCount"]!.GetValue<int>());
        Assert.Equal(1, documentsDiagnostics["failureCount"]!.GetValue<int>());
        Assert.False(documentsDiagnostics["anyNeedsReplan"]!.GetValue<bool>());
        Assert.False(documentsDiagnostics["allFailuresRetryable"]!.GetValue<bool>());
        Assert.NotNull(documentsDiagnostics["failedUrls"]);
        var failedUrlNode = Assert.Single(Assert.IsType<JsonArray>(documentsDiagnostics["failedUrls"]!));
        Assert.NotNull(failedUrlNode);
        Assert.Equal("https://example.com/b", failedUrlNode!.GetValue<string>());
    }

    [Fact]
    public async Task RuntimePlanExecutor_DoesNotPoisonMappedPerItemLlmStepAfterPartialMappedToolFailure_AndPassesDiagnostics()
    {
        var llmClient = new CapturingPlanningLlmClient(inputs =>
        {
            var document = Assert.IsType<JsonObject>(inputs["document"]);
            var title = document["title"]!.GetValue<string>();
            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new
            {
                model = title,
                summary = $"Structured specs for {title}"
            }));
        });

        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateSeedPagesTool(), CreatePartiallyFailingDownloadTool()]);

        var result = await executor.ExecuteAsync(CreateMappedDownloadThenExtractRuntimePlan());

        Assert.True(result.Succeeded);
        Assert.Equal("executed", result.Status);

        var structuredSpecs = Assert.IsType<JsonArray>(result.FinalOutput);
        Assert.Equal(2, structuredSpecs.Count);
        Assert.Equal(2, llmClient.CapturedInputs.Count);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("mapped_tool_item_failed", issue.Code);
        Assert.False(issue.IsBlocking);

        foreach (var capturedInput in llmClient.CapturedInputs)
        {
            Assert.NotNull(capturedInput["document"]);
            var inputDiagnostics = Assert.IsType<JsonObject>(capturedInput["_inputDiagnostics"]);
            var documentDiagnostics = Assert.IsType<JsonObject>(inputDiagnostics["document"]!);
            Assert.Equal(3, documentDiagnostics["totalItems"]!.GetValue<int>());
            Assert.Equal(2, documentDiagnostics["successCount"]!.GetValue<int>());
            Assert.Equal(1, documentDiagnostics["failureCount"]!.GetValue<int>());
            Assert.False(documentDiagnostics["anyNeedsReplan"]!.GetValue<bool>());
            Assert.False(documentDiagnostics["allFailuresRetryable"]!.GetValue<bool>());
            var failedUrlNode = Assert.Single(Assert.IsType<JsonArray>(documentDiagnostics["failedUrls"]!));
            Assert.Equal("https://example.com/b", failedUrlNode!.GetValue<string>());
        }
    }

    [Fact]
    public async Task RuntimePlanExecutor_ShapesStructuredLlmFailure_AfterPartialMappedToolFailure_WithoutBindingValueMissing()
    {
        var llmClient = new CapturingPlanningLlmClient(_ => ResultEnvelope<JsonElement?>.Failure(
            "insufficient_evidence",
            "Reliable comparison is impossible with incomplete source coverage.",
            JsonSerializer.SerializeToElement(new
            {
                status = "partial",
                needsReplan = true,
                type = "error",
                details = new[] { "Only part of the candidate evidence was downloaded successfully." }
            })));
        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateSeedPagesTool(), CreatePartiallyFailingDownloadTool()]);

        var result = await executor.ExecuteAsync(CreateMappedDownloadThenSummarizeRuntimePlan());

        Assert.False(result.Succeeded);
        Assert.Equal("execution_failed", result.Status);
        Assert.Contains(result.Issues, issue => string.Equals(issue.Code, "mapped_tool_item_failed", StringComparison.Ordinal));
        var blockingIssue = Assert.Single(result.Issues, issue => issue.IsBlocking);
        Assert.Equal("insufficient_evidence", blockingIssue.Code);
        Assert.NotNull(blockingIssue.Details);
        Assert.Equal("partial", blockingIssue.Details!.Value.GetProperty("status").GetString());
        Assert.DoesNotContain(result.Issues, issue => string.Equals(issue.Code, "binding_value_missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuiltInWebDownloadLogic_DoesNotRetryForbiddenResponses()
    {
        var handler = new CountingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<WebToolException>(() => BuiltInWebToolLogic.DownloadAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebDownloadInput(Url: "https://example.com/protected")));

        Assert.Equal("download_http_error", exception.Code);
        Assert.False(exception.Details.Retryable);
        Assert.Equal(1, handler.CallCount);
    }

    private static RuntimePlan CreateMappedDownloadRuntimePlan() =>
        new()
        {
            Goal = "Download candidate pages with partial diagnostics.",
            ResultStepId = "download_pages",
            ResultPort = "documents",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "seed_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:seed-pages",
                    Purpose = "Provide three candidate pages.",
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "pages",
                            SemanticType = "reference[]"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "download_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:download-page",
                    Purpose = "Download each candidate page.",
                    Fanout = LowLevelFanoutModes.PerItem,
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["page"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$seed_pages.pages",
                            Mode = LowLevelInputModes.Map
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "documents",
                            SemanticType = "document[]"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    },
                    IsResult = true
                }
            ]
        };

    private static RuntimePlan CreateMappedDownloadThenSummarizeRuntimePlan() =>
        new()
        {
            Goal = "Download candidate pages and summarize the surviving evidence.",
            ResultStepId = "summarize_pages",
            ResultPort = "summary",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "seed_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:seed-pages",
                    Purpose = "Provide three candidate pages.",
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "pages",
                            SemanticType = "reference[]"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "download_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:download-page",
                    Purpose = "Download each candidate page.",
                    Fanout = LowLevelFanoutModes.PerItem,
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["page"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$seed_pages.pages",
                            Mode = LowLevelInputModes.Map
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "documents",
                            SemanticType = "document[]"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "summarize_pages",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Summarize the downloaded evidence without guessing missing facts.",
                    Instruction = "Produce a concise summary from the downloaded documents only.",
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["documents"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$download_pages.documents",
                            Mode = LowLevelInputModes.Value
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "summary",
                            SemanticType = "summary"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.String
                    },
                    IsResult = true
                }
            ]
        };

    private static RuntimePlan CreateMappedDownloadThenExtractRuntimePlan() =>
        new()
        {
            Goal = "Download candidate pages and extract structured per-item evidence.",
            ResultStepId = "extract_pages",
            ResultPort = "structured",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "seed_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:seed-pages",
                    Purpose = "Provide three candidate pages.",
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "pages",
                            SemanticType = "reference[]"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "download_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:download-page",
                    Purpose = "Download each candidate page.",
                    Fanout = LowLevelFanoutModes.PerItem,
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["page"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$seed_pages.pages",
                            Mode = LowLevelInputModes.Map
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "documents",
                            SemanticType = "document[]"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "extract_pages",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Extract structured evidence from each surviving document independently.",
                    Instruction = "Return a compact JSON object for the current document only.",
                    Fanout = LowLevelFanoutModes.PerItem,
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["document"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$download_pages.documents",
                            Mode = LowLevelInputModes.Map
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "structured",
                            SemanticType = "structured document"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    },
                    IsResult = true
                }
            ]
        };

    private static AppToolDescriptor CreateSeedPagesTool() =>
        CreateTool(
            serverName: "mock-web",
            toolName: "seed-pages",
            description: "Seed candidate pages.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {}
                }
                """,
            outputSchemaJson: """
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
            execute: _ => new
            {
                pages = new[]
                {
                    new { url = "https://example.com/a", title = "Item A" },
                    new { url = "https://example.com/b", title = "Item B" },
                    new { url = "https://example.com/c", title = "Item C" }
                }
            });

    private static AppToolDescriptor CreatePartiallyFailingDownloadTool() =>
        CreateTool(
            serverName: "mock-web",
            toolName: "download-page",
            description: "Download a page with one synthetic failure.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "page": {
                      "type": "object",
                      "properties": {
                        "url": { "type": "string" },
                        "title": { "type": "string" }
                      },
                      "required": ["url", "title"]
                    }
                  },
                  "required": ["page"]
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
                var url = GetRequiredString(page, "url");
                var title = GetRequiredString(page, "title");
                if (string.Equals(url, "https://example.com/b", StringComparison.Ordinal))
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content =
                        [
                            new TextContentBlock
                            {
                                Text = "Download failed with HTTP 403 Forbidden."
                            }
                        ],
                        StructuredContent = JsonSerializer.SerializeToNode(new
                        {
                            code = "download_http_error",
                            message = "Download failed with HTTP 403 Forbidden.",
                            url,
                            retryable = false,
                            httpStatusCode = 403
                        })
                    };
                }

                return new
                {
                    url,
                    title,
                    content = $"Downloaded content for {title}"
                };
            });

    private static AppToolDescriptor CreateAlwaysFailingDownloadTool() =>
        CreateTool(
            serverName: "mock-web",
            toolName: "download-page",
            description: "Always fail page downloads.",
            inputSchemaJson: """
                {
                  "type": "object",
                  "properties": {
                    "page": {
                      "type": "object",
                      "properties": {
                        "url": { "type": "string" },
                        "title": { "type": "string" }
                      },
                      "required": ["url", "title"]
                    }
                  },
                  "required": ["page"]
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
                var url = GetRequiredString(page, "url");
                return new CallToolResult
                {
                    IsError = true,
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = "Download failed with HTTP 403 Forbidden."
                        }
                    ],
                    StructuredContent = JsonSerializer.SerializeToNode(new
                    {
                        code = "download_http_error",
                        message = "Download failed with HTTP 403 Forbidden.",
                        url,
                        retryable = false,
                        httpStatusCode = 403
                    })
                };
            });

    private static AppToolDescriptor CreateTool(
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

    private static Dictionary<string, object?> GetRequiredObject(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is not Dictionary<string, object?> result)
            throw new InvalidOperationException($"Expected argument '{key}' to be an object.");

        return result;
    }

    private static string GetRequiredString(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Expected argument '{key}' to be a non-empty string.");

        return text;
    }

    private sealed class ThrowingPlanningLlmClient : IPlanningLlmClient
    {
        public Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"LLM execution is not expected in this test for agent '{agentName}'.");
    }

    private sealed class CapturingPlanningLlmClient(Func<JsonObject, ResultEnvelope<JsonElement?>> handler) : IPlanningLlmClient
    {
        public List<JsonObject> CapturedInputs { get; } = [];

        public Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            var inputs = ExtractInputsFromPrompt(userPrompt);
            CapturedInputs.Add((JsonObject)inputs.DeepClone());
            return Task.FromResult(handler(inputs));
        }
    }

    private static JsonObject ExtractInputsFromPrompt(string userPrompt)
    {
        const string marker = "Inputs:";
        var markerIndex = userPrompt.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            throw new InvalidOperationException("Prompt does not contain an Inputs section.");

        var jsonText = userPrompt[(markerIndex + marker.Length)..].Trim();
        return JsonNode.Parse(jsonText)?.AsObject()
            ?? throw new InvalidOperationException("Prompt inputs are not a JSON object.");
    }

    private sealed class CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(responseFactory(request));
        }
    }
}
