using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using Microsoft.Extensions.AI;
using Moq;
using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Tests;

public sealed class RuntimeLlmPromptOverflowTests
{
    private const string OverflowMessage = """
        HTTP 400 (invalid_request_error: context_length_exceeded)
        Parameter: messages

        Input tokens exceed the configured limit of 272000 tokens. Your messages resulted in 312677 tokens.
        """;

    [Fact]
    public async Task RuntimePlanExecutor_TruncatesBuiltInWebContentOnlyInPrompt_AndKeepsStoredToolOutputIntact()
    {
        var events = new List<PlanRunEvent>();
        var content = CreateLongContent();
        var llmClient = new PromptCapturingPlanningLlmClient(
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement("summary")));
        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateBuiltInWebDownloadTool(content)],
            planRunObserver: new ActionPlanRunObserver(events.Add));

        var result = await executor.ExecuteAsync(CreateSingleDownloadThenSummarizePlan());

        Assert.True(result.Succeeded);
        Assert.Equal("summary", Assert.IsAssignableFrom<JsonValue>(result.FinalOutput).GetValue<string>());

        var promptInputs = Assert.Single(llmClient.CapturedInputs);
        var document = Assert.IsType<JsonObject>(promptInputs["document"]);
        Assert.Equal("https://example.com/page", document["url"]!.GetValue<string>());
        Assert.Equal("Example title", document["title"]!.GetValue<string>());
        var promptContent = document["content"]!.GetValue<string>();
        Assert.StartsWith(new string('A', 8000), promptContent, StringComparison.Ordinal);
        Assert.EndsWith(new string('Z', 4000), promptContent, StringComparison.Ordinal);
        Assert.Contains("[web-content-truncated", promptContent, StringComparison.Ordinal);

        var downloadCompleted = Assert.Single(
            events.OfType<RuntimeStepCompletedEvent>(),
            evt => string.Equals(evt.StepId, "download_page", StringComparison.Ordinal));
        var storedContent = downloadCompleted.Output!.Value
            .GetProperty("document")
            .GetProperty("content")
            .GetString();
        Assert.Equal(content.Length, storedContent!.Length);
        Assert.DoesNotContain("[web-content-truncated", storedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimePlanExecutor_TruncatesEachBuiltInWebDocumentInArray_WithoutChangingShape()
    {
        var llmClient = new PromptCapturingPlanningLlmClient(
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement("summary")));
        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateSeedPagesTool(), CreateBuiltInWebDownloadByPageTool(CreateLongContent())]);

        var result = await executor.ExecuteAsync(CreateMappedDownloadThenSummarizePlan());

        Assert.True(result.Succeeded);
        var promptInputs = Assert.Single(llmClient.CapturedInputs);
        var documents = Assert.IsType<JsonArray>(promptInputs["documents"]);
        Assert.Equal(2, documents.Count);

        foreach (var documentNode in documents)
        {
            var document = Assert.IsType<JsonObject>(documentNode);
            Assert.NotNull(document["url"]);
            Assert.NotNull(document["title"]);
            var content = document["content"]!.GetValue<string>();
            Assert.Contains("[web-content-truncated", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RuntimePlanExecutor_PreflightOverflowAfterRetryTruncation_DoesNotCallModel()
    {
        var llmClient = new CountingPlanningLlmClient(
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement("unused")));
        var options = new RuntimeLlmPromptingOptions
        {
            SoftPromptChars = 1000
        };
        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateBuiltInWebDownloadTool(CreateLongContent())],
            runtimeLlmPromptingOptions: options);

        var result = await executor.ExecuteAsync(CreateSingleDownloadThenSummarizePlan());

        Assert.False(result.Succeeded);
        Assert.Equal(0, llmClient.CallCount);
        var issue = Assert.Single(result.Issues, static issue => issue.IsBlocking);
        Assert.Equal(RuntimeLlmPromptOverflow.ErrorCode, issue.Code);
        Assert.True(issue.Details!.Value.GetProperty("retryAttempted").GetBoolean());
    }

    [Fact]
    public async Task RuntimePlanExecutor_RetriesOnceWithStricterPromptAfterProviderOverflow()
    {
        var capturedUserPrompts = new List<string>();
        var chatClient = CreateMockChatClient(
            capturedUserPrompts,
            new ClientResultException(OverflowMessage, null, null),
            CreateEnvelopeResponse("summary"));
        var executor = new RuntimePlanExecutor(
            new ChatClientPlanningLlmClient(chatClient.Object),
            [CreateBuiltInWebDownloadTool(CreateLongContent())]);

        var result = await executor.ExecuteAsync(CreateSingleDownloadThenSummarizePlan());

        Assert.True(result.Succeeded);
        Assert.Equal(2, capturedUserPrompts.Count);
        Assert.True(capturedUserPrompts[1].Length < capturedUserPrompts[0].Length);
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RuntimePlanExecutor_StopsAfterSecondProviderOverflow_AndKeepsStructuredIssue()
    {
        var capturedUserPrompts = new List<string>();
        var chatClient = CreateMockChatClient(
            capturedUserPrompts,
            new ClientResultException(OverflowMessage, null, null),
            new ClientResultException(OverflowMessage, null, null));
        var executor = new RuntimePlanExecutor(
            new ChatClientPlanningLlmClient(chatClient.Object),
            [CreateBuiltInWebDownloadTool(CreateLongContent())]);

        var result = await executor.ExecuteAsync(CreateSingleDownloadThenSummarizePlan());

        Assert.False(result.Succeeded);
        Assert.Equal(2, capturedUserPrompts.Count);
        var issue = Assert.Single(result.Issues, static issue => issue.IsBlocking);
        Assert.Equal(RuntimeLlmPromptOverflow.ErrorCode, issue.Code);
        Assert.False(issue.Details!.Value.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain(result.Issues, static issue => string.Equals(issue.Code, "llm_call_exception", StringComparison.Ordinal));
        chatClient.Verify(
            client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RuntimePlanExecutor_LargeNonWebPayloadFailsWithoutPromptTruncation()
    {
        var llmClient = new CountingPlanningLlmClient(
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement("unused")));
        var options = new RuntimeLlmPromptingOptions
        {
            SoftPromptChars = 1000
        };
        var executor = new RuntimePlanExecutor(
            llmClient,
            [CreateNonWebContentTool(CreateLongContent())],
            runtimeLlmPromptingOptions: options);

        var result = await executor.ExecuteAsync(CreateSingleNonWebThenSummarizePlan());

        Assert.False(result.Succeeded);
        Assert.Equal(0, llmClient.CallCount);
        var issue = Assert.Single(result.Issues, static issue => issue.IsBlocking);
        Assert.Equal(RuntimeLlmPromptOverflow.ErrorCode, issue.Code);
        Assert.False(issue.Details!.Value.GetProperty("truncationAttempted").GetBoolean());
        Assert.Equal(0, issue.Details!.Value.GetProperty("truncatedValueCount").GetInt32());
        Assert.Equal(0, issue.Details!.Value.GetProperty("truncatedInputNames").GetArrayLength());
        Assert.DoesNotContain(result.Issues, static issue => string.Equals(issue.Code, "llm_call_exception", StringComparison.Ordinal));
    }

    private static RuntimePlan CreateSingleDownloadThenSummarizePlan() =>
        new()
        {
            Goal = "Download one page and summarize it.",
            ResultStepId = "summarize_page",
            ResultPort = "summary",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "download_page",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "test-web:download",
                    Purpose = "Download a web page.",
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["url"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Literal,
                            Literal = JsonValue.Create("https://example.com/page")
                        }
                    },
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "document",
                            SemanticType = "document"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "summarize_page",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Summarize the document.",
                    Instruction = "Summarize the supplied document only.",
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["document"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$download_page.document",
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

    private static RuntimePlan CreateMappedDownloadThenSummarizePlan() =>
        new()
        {
            Goal = "Download multiple pages and summarize them.",
            ResultStepId = "summarize_pages",
            ResultPort = "summary",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "seed_pages",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-web:seed-pages",
                    Purpose = "Provide candidate pages.",
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
                    CapabilityId = "test-web:download",
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
                    Purpose = "Summarize the downloaded documents.",
                    Instruction = "Summarize the supplied documents only.",
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

    private static RuntimePlan CreateSingleNonWebThenSummarizePlan() =>
        new()
        {
            Goal = "Read non-web content and summarize it.",
            ResultStepId = "summarize_data",
            ResultPort = "summary",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "load_data",
                    Kind = LowLevelStepKinds.Tool,
                    CapabilityId = "mock-data:load",
                    Purpose = "Load a large non-web payload.",
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "payload",
                            SemanticType = "document"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    }
                },
                new RuntimeStep
                {
                    Id = "summarize_data",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Summarize the payload.",
                    Instruction = "Summarize the supplied payload only.",
                    In = new Dictionary<string, RuntimeInputValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["payload"] = new RuntimeInputValue
                        {
                            Kind = RuntimeInputValueKinds.Binding,
                            From = "$load_data.payload",
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

    private static AppToolDescriptor CreateBuiltInWebDownloadTool(string content) =>
        new(
            QualifiedName: "test-web:download",
            ServerName: "Built-in Web",
            ToolName: "download",
            DisplayName: "download",
            Description: "Download a web page.",
            InputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" }
                  },
                  "required": ["url"]
                }
                """),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" },
                    "title": { "type": "string" },
                    "content": { "type": "string" }
                  },
                  "required": ["url", "title", "content"]
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: (arguments, _) => Task.FromResult<object>(new
            {
                url = GetRequiredString(arguments, "url"),
                title = "Example title",
                content
            }),
            BaseQualifiedName: "built-in-web:download",
            BaseServerName: "built-in-web");

    private static AppToolDescriptor CreateBuiltInWebDownloadByPageTool(string content) =>
        new(
            QualifiedName: "test-web:download",
            ServerName: "Built-in Web",
            ToolName: "download",
            DisplayName: "download",
            Description: "Download a web page.",
            InputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "page": { "type": "object" }
                  },
                  "required": ["page"]
                }
                """),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" },
                    "title": { "type": "string" },
                    "content": { "type": "string" }
                  },
                  "required": ["url", "title", "content"]
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: (arguments, _) =>
            {
                var page = GetRequiredObject(arguments, "page");
                return Task.FromResult<object>(new
                {
                    url = GetRequiredString(page, "url"),
                    title = GetRequiredString(page, "title"),
                    content
                });
            },
            BaseQualifiedName: "built-in-web:download",
            BaseServerName: "built-in-web");

    private static AppToolDescriptor CreateSeedPagesTool() =>
        new(
            QualifiedName: "mock-web:seed-pages",
            ServerName: "Mock Web",
            ToolName: "seed-pages",
            DisplayName: "seed-pages",
            Description: "Seed candidate pages.",
            InputSchema: ParseJson("""{ "type": "object", "properties": {} }"""),
            OutputSchema: ParseJson(
                """
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
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: (_, _) => Task.FromResult<object>(new
            {
                pages = new[]
                {
                    new { url = "https://example.com/a", title = "Page A" },
                    new { url = "https://example.com/b", title = "Page B" }
                }
            }));

    private static AppToolDescriptor CreateNonWebContentTool(string content) =>
        new(
            QualifiedName: "mock-data:load",
            ServerName: "Mock Data",
            ToolName: "load",
            DisplayName: "load",
            Description: "Load a non-web payload.",
            InputSchema: ParseJson("""{ "type": "object", "properties": {} }"""),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string" },
                    "content": { "type": "string" }
                  },
                  "required": ["title", "content"]
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: (_, _) => Task.FromResult<object>(new
            {
                title = "Local payload",
                content
            }));

    private static string CreateLongContent() =>
        string.Concat(
            new string('A', 9000),
            new string('M', 5000),
            new string('Z', 5000));

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

    private static Mock<IChatClient> CreateMockChatClient(List<string> capturedUserPrompts, params object[] outcomes)
    {
        var queue = new Queue<object>(outcomes);
        var chatClient = new Mock<IChatClient>(MockBehavior.Strict);

        chatClient
            .Setup(client => client.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, _, _) =>
            {
                var prompt = messages.LastOrDefault(static message => message.Role == ChatRole.User)?.Text
                    ?? throw new InvalidOperationException("User prompt was not captured.");
                capturedUserPrompts.Add(prompt);

                var next = queue.Dequeue();
                if (next is Exception exception)
                    throw exception;

                return Task.FromResult((ChatResponse)next);
            });

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

    private static ChatResponse CreateEnvelopeResponse(string result)
    {
        var json = JsonSerializer.Serialize(
            ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(result)),
            PlanningNodeJson.SerializerOptions);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
    }

    private static JsonElement ParseJson(string json)
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

    private sealed class PromptCapturingPlanningLlmClient(ResultEnvelope<JsonElement?> response) : IPlanningLlmClient
    {
        public List<JsonObject> CapturedInputs { get; } = [];

        public Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            CapturedInputs.Add(ExtractInputsFromPrompt(userPrompt));
            return Task.FromResult(response);
        }
    }

    private sealed class CountingPlanningLlmClient(ResultEnvelope<JsonElement?> response) : IPlanningLlmClient
    {
        public int CallCount { get; private set; }

        public Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }
}
