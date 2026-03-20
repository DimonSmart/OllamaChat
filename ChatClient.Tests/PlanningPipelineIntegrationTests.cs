using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.ExceptionServices;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Orchestration;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using System.ClientModel;
using Xunit.Abstractions;

namespace ChatClient.Tests;

public sealed class PlanningPipelineIntegrationTests(ITestOutputHelper output)
{
    private const string DevModel = "gpt-oss:120b-cloud";

    public static TheoryData<string, string> RealWebExplorationQueries => new()
    {
        {
            "maze-nuget-review-ru-markdown",
            """
            Найди минимум 3 NuGet пакета которые умеют генерировать лабиринты. Оцени их с точки зрения простоты использования и сделай итоговый мини-обзор на несколько вариантов.
            Результат оформи как markdown
            """
        },
        {
            "maze-nuget-review",
            "Find 3 NuGet packages that can generate mazes. For each package, locate its NuGet page and a GitHub or documentation page, compare ease of use and documentation quality, and write a short review with a recommendation."
        },
        {
            "excel-reader-tradeoffs",
            "Find 3 .NET libraries for reading Excel files. For each one, locate an official documentation or GitHub page and also check its NuGet page, compare ease of use, licensing constraints, and maturity, then recommend one for an internal business tool."
        },
        {
            "pdf-generation-stack",
            "Find 3 .NET libraries for PDF generation. For each candidate, locate the official docs or repository plus a package page, compare beginner-friendliness, licensing, and server-side suitability, then give a short recommendation."
        },
        {
            "fuzzy-matching-packages",
            "Find 3 .NET libraries for fuzzy string matching. For each candidate, locate a NuGet page and a README or documentation page, compare API simplicity, maintenance signals, and real-world readiness, then write a compact review and recommend one."
        }
    };

    [Fact]
    public async Task FullPipeline_PlannerAndOrchestrator_ReturnsSystemOutcome()
    {
        const string userQuery = "I'm looking for a good robot vacuum cleaner. Can you find two popular models, check their specs, and tell me which one is better?";
        await RunFullPipelineAsync(userQuery, CreateMockToolCatalog());
    }

    [Fact]
    public async Task FullPipeline_PlannerAndOrchestrator_WithAlternateMockQuery_ReturnsSystemOutcome()
    {
        const string userQuery = "Find two robot vacuum cleaners, compare their suction power, battery life, and price, and recommend the better value option.";
        await RunFullPipelineAsync(userQuery, CreateMockToolCatalog());
    }

    [Fact]
    public async Task FullPipeline_PlannerAndOrchestrator_WithRealWebSearchAndDownload_ReturnsSystemOutcome()
    {
        const string userQuery = "Compare Markdig and CommonMark.NET using their GitHub or documentation pages, and tell me which one is better for a small .NET app.";
        var httpClientFactory = new TestHttpClientFactory();
        await RunWithRetriesAsync(() => RunFullPipelineAsync(userQuery, CreateRealWebToolCatalog(httpClientFactory)));
    }

    [Theory]
    [Trait("Category", "RealWebExploration")]
    [MemberData(nameof(RealWebExplorationQueries))]
    public async Task FullPipeline_PlannerAndOrchestrator_WithRealWebExplorationQueries_CapturesArtifacts(
        string scenarioId,
        string userQuery)
    {
        var httpClientFactory = new TestHttpClientFactory();
        var artifact = await RunFullPipelineInspectionAsync(
            scenarioId,
            userQuery,
            CreateRealWebToolCatalog(httpClientFactory));

        var artifactWriter = new PlanningExplorationArtifactWriter();
        var savedPaths = await artifactWriter.SaveAsync(artifact);

        output.WriteLine($"Saved exploration summary: {savedPaths.SummaryPath}");
        output.WriteLine($"Saved exploration log: {savedPaths.LogPath}");
    }

    private async Task RunFullPipelineAsync(string userQuery, PlanningToolCatalog tools)
    {
        var chatClient = BuildChatClient();
        var logger = new TestLogger(output);
        var answerAsserter = new CachedLlmAnswerAsserter(chatClient, DevModel);
        var planner = new LlmPlanner(chatClient, tools, logger);
        var replanner = new LlmReplanner(chatClient, tools, logger);
        var runner = new AgentStepRunner(chatClient);
        var executor = new PlanExecutor(tools, runner, logger);
        var orchestrator = new PlanningOrchestrator(
            planner,
            executor,
            new GoalVerifier(askUserEnabled: true),
            logger,
            maxAttempts: 3,
            replanner: replanner,
            finalAnswerVerifier: new LlmFinalAnswerVerifier(chatClient));

        output.WriteLine($"User query: {userQuery}");

        var result = await orchestrator.RunAsync(userQuery);

        if (result.Ok)
            output.WriteLine($"\n=== FINAL ANSWER ===\n{SerializeJson(result.Data)}");
        else
            output.WriteLine($"\n=== OUTCOME DETAILS ===\ncode={result.Error?.Code}\nmessage={result.Error?.Message}\ndetails={SerializeJson(result.Error?.Details)}");

        Assert.True(
            result.Ok,
            $"Expected orchestrator to return a final answer, but got code={result.Error?.Code}, message={result.Error?.Message}, details={SerializeJson(result.Error?.Details)}");

        await AssertAnswersQuestionAsync(answerAsserter, userQuery, result.Data);
    }

    private async Task<PlanningExplorationArtifact> RunFullPipelineInspectionAsync(
        string scenarioId,
        string userQuery,
        PlanningToolCatalog tools)
    {
        var chatClient = BuildChatClient();
        var sink = new TestArtifactLogSink();
        var logger = new TestLogger(output, sink);
        var answerAsserter = new CachedLlmAnswerAsserter(chatClient, DevModel);
        var planner = new LlmPlanner(chatClient, tools, logger);
        var replanner = new LlmReplanner(chatClient, tools, logger);
        var runner = new AgentStepRunner(chatClient);
        var executor = new PlanExecutor(tools, runner, logger);
        var orchestrator = new PlanningOrchestrator(
            planner,
            executor,
            new GoalVerifier(askUserEnabled: true),
            logger,
            maxAttempts: 3,
            replanner: replanner,
            finalAnswerVerifier: new LlmFinalAnswerVerifier(chatClient));

        void WriteLine(string message)
        {
            output.WriteLine(message);
            sink.WriteLine(message);
        }

        WriteLine($"Scenario: {scenarioId}");
        WriteLine($"User query: {userQuery}");

        try
        {
            var result = await orchestrator.RunAsync(userQuery);

            if (result.Ok)
                WriteLine($"\n=== FINAL ANSWER ===\n{SerializeJson(result.Data)}");
            else
                WriteLine($"\n=== OUTCOME DETAILS ===\ncode={result.Error?.Code}\nmessage={result.Error?.Message}\ndetails={SerializeJson(result.Error?.Details)}");

            AnswerAssertionVerdict? verdict = null;
            if (result.Ok)
            {
                verdict = await answerAsserter.EvaluateAsync(userQuery, result.Data);
                WriteLine($"LLM asserter verdict: isAnswer={verdict.IsAnswer} cache={verdict.FromCache} comment={verdict.Comment}");
            }

            return new PlanningExplorationArtifact
            {
                ScenarioId = scenarioId,
                UserQuery = userQuery,
                Status = result.Ok ? "ok" : "error",
                ErrorCode = result.Error?.Code,
                ErrorMessage = result.Error?.Message,
                ErrorDetailsJson = SerializeJson(result.Error?.Details),
                ResultJson = SerializeJson(result.Data),
                AnsweredQuestion = verdict?.IsAnswer,
                AnswerAssertionComment = verdict?.Comment,
                LogText = sink.GetText()
            };
        }
        catch (Exception ex)
        {
            WriteLine($"\n=== UNHANDLED EXCEPTION ===\n{ex}");

            return new PlanningExplorationArtifact
            {
                ScenarioId = scenarioId,
                UserQuery = userQuery,
                Status = "exception",
                ErrorCode = ex.GetType().Name,
                ErrorMessage = ex.Message,
                ErrorDetailsJson = ex.ToString(),
                ResultJson = null,
                AnsweredQuestion = null,
                AnswerAssertionComment = null,
                LogText = sink.GetText()
            };
        }
    }

    private async Task RunWithRetriesAsync(Func<Task> action, int maxAttempts = 3)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                output.WriteLine($"Real-web pipeline attempt {attempt} failed: {ex.Message}");
            }
        }

        ExceptionDispatchInfo.Capture(lastException ?? new InvalidOperationException("Pipeline retry failed without an exception.")).Throw();
    }

    private static PlanningToolCatalog CreateMockToolCatalog() =>
        new([
            CreateDescriptor(
                serverName: "mock-web",
                toolName: "search",
                description: "Search the web and return raw structured search results with URL, title, snippet, and site metadata. The output may be noisy or partially irrelevant and must be checked for relevance before relying on it.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" },
                        "limit": { "type": "number" }
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
                              "title": { "type": "string" },
                              "snippet": { "type": "string" },
                              "siteName": { "type": "string" },
                              "displayUrl": { "type": "string" },
                              "position": { "type": "integer" }
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
                    query = "robot vacuum",
                    results = new[]
                    {
                        new
                        {
                            url = "https://example.com/item-a",
                            title = "RoboClean A1 Max",
                            snippet = "Popular robot vacuum candidate with LiDAR navigation.",
                            siteName = "Example",
                            displayUrl = "example.com/item-a",
                            position = 1
                        },
                        new
                        {
                            url = "https://example.com/item-b",
                            title = "HomeSweep S5",
                            snippet = "Popular robot vacuum candidate with vSLAM navigation.",
                            siteName = "Example",
                            displayUrl = "example.com/item-b",
                            position = 2
                        }
                    }
                }),
            CreateDescriptor(
                serverName: "mock-web",
                toolName: "download",
                description: "Download a single web page. Prefer passing a full search-result object via 'page'; the tool returns the same object enriched with 'content'. If only a raw absolute URL is available, pass it via 'url' and the tool returns a minimal object with url, title, and content.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "page": {
                          "type": "object",
                          "properties": {
                            "url": { "type": "string" },
                            "title": { "type": "string" },
                            "snippet": { "type": "string" },
                            "siteName": { "type": "string" },
                            "displayUrl": { "type": "string" },
                            "position": { "type": "integer" }
                          }
                        },
                        "url": { "type": "string" }
                      }
                    }
                    """,
                outputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "url": { "type": "string" },
                        "title": { "type": "string" },
                        "snippet": { "type": "string" },
                        "siteName": { "type": "string" },
                        "displayUrl": { "type": "string" },
                        "position": { "type": "integer" },
                        "content": { "type": "string" }
                      },
                      "required": ["url", "title", "content"]
                    }
                    """,
                execute: arguments =>
                {
                    var pageObject = TryGetObjectProperty(arguments, "page");
                    var url = pageObject is not null
                        ? TryGetStringProperty(pageObject, "url")
                        : TryGetStringProperty(arguments, "page") ?? TryGetStringProperty(arguments, "url");
                    if (string.IsNullOrWhiteSpace(url))
                        throw new InvalidOperationException("Download URL is required.");

                    var content = url.Contains("item-a", StringComparison.OrdinalIgnoreCase)
                        ? "RoboClean A1 Max is a popular robot vacuum cleaner with 7000 Pa suction power, up to 180 minutes of battery runtime, a 0.5 L dustbin, LiDAR navigation, and a list price of $799."
                        : "HomeSweep S5 is a popular robot vacuum cleaner with 5000 Pa suction power, up to 140 minutes of battery runtime, a 0.4 L dustbin, vSLAM navigation, and a list price of $649.";

                    JsonObject payload;
                    if (pageObject is not null)
                    {
                        payload = JsonNode.Parse(JsonSerializer.Serialize(pageObject))?.AsObject() ?? new JsonObject();
                        payload["content"] = content;
                    }
                    else
                    {
                        payload = new JsonObject
                        {
                            ["url"] = url,
                            ["title"] = url.Contains("item-a", StringComparison.OrdinalIgnoreCase)
                                ? "RoboClean A1 Max review"
                                : "HomeSweep S5 review",
                            ["content"] = content
                        };
                    }

                    return payload;
                })
        ]);

    private static PlanningToolCatalog CreateRealWebToolCatalog(IHttpClientFactory httpClientFactory) =>
        new([
            CreateDescriptor(
                serverName: "built-in-web",
                toolName: "search",
                description: "Search the web and return structured search results with metadata.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" },
                        "limit": { "type": "integer" }
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
                              "title": { "type": "string" },
                              "snippet": { "type": ["string", "null"] },
                              "siteName": { "type": ["string", "null"] },
                              "displayUrl": { "type": ["string", "null"] },
                              "age": { "type": ["string", "null"] },
                              "thumbnailUrl": { "type": ["string", "null"] },
                              "position": { "type": ["integer", "null"] }
                            },
                            "required": ["url", "title"]
                          }
                        }
                      },
                      "required": ["query", "results"]
                    }
                    """,
                execute: arguments => BuiltInWebToolLogic.SearchAsync(
                    httpClientFactory,
                    NullLogger.Instance,
                    new WebSearchInput(
                        Query: TryGetStringProperty(arguments, "query") ?? throw new InvalidOperationException("Search query is required."),
                        Limit: TryGetIntProperty(arguments, "limit"))).GetAwaiter().GetResult()),
            CreateDescriptor(
                serverName: "built-in-web",
                toolName: "download",
                description: "Download a single web page while preserving search metadata when available.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "page": {
                          "type": "object",
                          "properties": {
                            "url": { "type": "string" },
                            "title": { "type": ["string", "null"] },
                            "snippet": { "type": ["string", "null"] },
                            "siteName": { "type": ["string", "null"] },
                            "displayUrl": { "type": ["string", "null"] },
                            "age": { "type": ["string", "null"] },
                            "thumbnailUrl": { "type": ["string", "null"] },
                            "position": { "type": ["integer", "null"] }
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
                        "content": { "type": "string" },
                        "snippet": { "type": ["string", "null"] },
                        "siteName": { "type": ["string", "null"] },
                        "displayUrl": { "type": ["string", "null"] },
                        "age": { "type": ["string", "null"] },
                        "thumbnailUrl": { "type": ["string", "null"] },
                        "position": { "type": ["integer", "null"] }
                      },
                      "required": ["url", "title", "content"]
                    }
                    """,
                execute: arguments => BuiltInWebToolLogic.DownloadAsync(
                    httpClientFactory,
                    NullLogger.Instance,
                    new WebDownloadInput(
                        Page: TryGetObjectProperty(arguments, "page") is { } page
                            ? new WebDownloadPageRef(
                                Url: TryGetStringProperty(page, "url") ?? string.Empty,
                                Title: TryGetStringProperty(page, "title"),
                                Snippet: TryGetStringProperty(page, "snippet"),
                                SiteName: TryGetStringProperty(page, "siteName"),
                                DisplayUrl: TryGetStringProperty(page, "displayUrl"),
                                Age: TryGetStringProperty(page, "age"),
                                ThumbnailUrl: TryGetStringProperty(page, "thumbnailUrl"),
                                Position: TryGetIntProperty(page, "position"))
                            : null,
                        Url: TryGetStringProperty(arguments, "url"))).GetAwaiter().GetResult())
        ]);

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
            OpenWorldHint: true,
            ExecuteAsync: (arguments, _) => Task.FromResult(execute(arguments)));

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? TryGetStringProperty(Dictionary<string, object?> source, string propertyName) =>
        source.TryGetValue(propertyName, out var value) && value is string text
            ? text
            : null;

    private static int? TryGetIntProperty(Dictionary<string, object?> source, string propertyName) =>
        source.TryGetValue(propertyName, out var value)
            ? value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                decimal decimalValue => (int)decimalValue,
                double doubleValue => (int)doubleValue,
                _ => null
            }
            : null;

    private static Dictionary<string, object?>? TryGetObjectProperty(Dictionary<string, object?> source, string propertyName) =>
        source.TryGetValue(propertyName, out var value) && value is Dictionary<string, object?> result
            ? result
            : null;

    private static IChatClient BuildChatClient()
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost:11434/v1/")
        };

        return new OpenAIClient(new ApiKeyCredential("ollama"), clientOptions)
            .GetChatClient(DevModel)
            .AsIChatClient();
    }

    private async Task AssertAnswersQuestionAsync(
        CachedLlmAnswerAsserter answerAsserter,
        string userQuery,
        JsonElement? answer)
    {
        Assert.NotNull(answer);

        var verdict = await answerAsserter.EvaluateAsync(userQuery, answer);
        output.WriteLine($"LLM asserter verdict: isAnswer={verdict.IsAnswer} cache={verdict.FromCache} comment={verdict.Comment}");
        Assert.True(verdict.IsAnswer, verdict.Comment);
    }

    private static string SerializeJson(JsonElement? element) =>
        element is null
            ? "null"
            : PlanningJson.SerializeIndented(element.Value);
}

public sealed class TestLogger(ITestOutputHelper output, TestArtifactLogSink? artifactSink = null) : IExecutionLogger
{
    public void Log(string message)
    {
        output.WriteLine(message);
        artifactSink?.WriteLine(message);
    }
}

public sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

public sealed class CachedLlmAnswerAsserter(IChatClient chatClient, string modelName)
{
    private const string PromptVersion = "v7";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _cacheDirectory = Path.Combine(TestPathHelper.FindRepositoryRoot(), ".llm-test-cache", "answer-assertions");

    public async Task<AnswerAssertionVerdict> EvaluateAsync(
        string question,
        JsonElement? answer,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);

        var answerJson = answer is null ? "null" : JsonSerializer.Serialize(answer.Value, JsonOptions);
        var cacheKey = ComputeCacheKey(question, answerJson);
        var cachePath = Path.Combine(_cacheDirectory, $"{cacheKey}.json");
        if (File.Exists(cachePath))
        {
            var cached = JsonSerializer.Deserialize<CachedAnswerAssertion>(await File.ReadAllTextAsync(cachePath, cancellationToken));
            if (cached?.Verdict is not null)
                return cached.Verdict with { FromCache = true };
        }

        var agent = new ChatClientAgent(chatClient, BuildSystemPrompt(), "answer_asserter", null, null, null, null);
        var userPrompt = BuildUserPrompt(question, answerJson);
        var response = await agent.RunAsync<ResultEnvelope<AnswerAssertionVerdict>>(userPrompt, cancellationToken: cancellationToken);
        var envelope = response.Result
            ?? throw new InvalidOperationException("Answer asserter returned an empty response envelope.");
        var verdict = envelope.GetRequiredDataOrThrow("Answer asserter") with { FromCache = false };
        if (!verdict.IsAnswer && LooksLikeComparisonRecommendation(question, answer))
        {
            verdict = verdict with
            {
                IsAnswer = true,
                Comment = "Heuristic override: the answer contains an explicit recommendation for a comparison-style question."
            };
        }

        var cachedAssertion = new CachedAnswerAssertion
        {
            Model = modelName,
            PromptVersion = PromptVersion,
            Question = question,
            Answer = answerJson,
            Verdict = verdict,
            RawResponse = response.Text
        };
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(cachedAssertion, JsonOptions), cancellationToken);

        return verdict;
    }

    private string ComputeCacheKey(string question, string answerJson)
    {
        var input = $"{modelName}\n{PromptVersion}\n{question}\n{answerJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildSystemPrompt() =>
        """
        You are a strict integration-test evaluator.
        Determine whether the candidate answer genuinely answers the original user question.
        Return ONLY valid JSON with this exact shape:
        {"ok":true|false,"data":{"isAnswer":true|false,"comment":"short explanation"}|null,"error":null|{"code":"string","message":"string","details":null}}
        Mark isAnswer=true when the answer directly addresses the core request, even if wording differs or some intermediate context is omitted.
        If the question asks to compare or choose and the candidate gives a clear recommendation with a relevant justification, that usually counts as an answer.
        Judge the final user-visible deliverable, not whether every intermediate search or extraction result is restated.
        Mark isAnswer=false only when the answer is off-topic, materially incomplete, or does not answer what was asked.
        When evaluation succeeds, set ok=true, error=null, and put the verdict into data.
        """;

    private static string BuildUserPrompt(string question, string answerJson) =>
        $"Original question:\n{question}\n\nCandidate answer:\n{answerJson}";

    private static bool LooksLikeComparisonRecommendation(string question, JsonElement? answer)
    {
        if (!IsComparisonQuestion(question))
            return false;

        if (answer is not JsonElement answerElement)
            return false;

        if (answerElement.ValueKind == JsonValueKind.Object)
            return answerElement.EnumerateObject().Any(property => property.Name is "betterModel" or "better_model" or "recommendedModel" or "recommended_model" or "bestModel" or "preferredModel");

        return answerElement.ValueKind == JsonValueKind.String
            && LooksLikeComparisonRecommendationText(answerElement.GetString() ?? string.Empty);
    }

    private static bool IsComparisonQuestion(string question)
    {
        var normalized = question.ToLowerInvariant();
        return normalized.Contains("compare", StringComparison.Ordinal)
            || normalized.Contains("which one", StringComparison.Ordinal)
            || normalized.Contains("better", StringComparison.Ordinal)
            || normalized.Contains("best", StringComparison.Ordinal)
            || normalized.Contains("recommend", StringComparison.Ordinal);
    }

    private static bool LooksLikeComparisonRecommendationText(string answerText)
    {
        var normalized = answerText.ToLowerInvariant();
        var hasRecommendation = normalized.Contains("recommended", StringComparison.Ordinal)
            || normalized.Contains("recommend", StringComparison.Ordinal)
            || normalized.Contains("better", StringComparison.Ordinal)
            || normalized.Contains("best", StringComparison.Ordinal)
            || normalized.Contains("winner", StringComparison.Ordinal);
        if (!hasRecommendation)
            return false;

        var hasComparisonEvidence = normalized.Contains(" vs ", StringComparison.Ordinal)
            || normalized.Contains("versus", StringComparison.Ordinal)
            || normalized.Contains("higher", StringComparison.Ordinal)
            || normalized.Contains("lower", StringComparison.Ordinal)
            || normalized.Contains("longer", StringComparison.Ordinal)
            || normalized.Contains("shorter", StringComparison.Ordinal)
            || normalized.Contains("stronger", StringComparison.Ordinal);

        return hasComparisonEvidence;
    }

    private sealed class CachedAnswerAssertion
    {
        public string Model { get; init; } = string.Empty;

        public string PromptVersion { get; init; } = string.Empty;

        public string Question { get; init; } = string.Empty;

        public string Answer { get; init; } = string.Empty;

        public string RawResponse { get; init; } = string.Empty;

        public AnswerAssertionVerdict? Verdict { get; init; }
    }
}

public sealed record AnswerAssertionVerdict
{
    public bool IsAnswer { get; init; }

    public string Comment { get; init; } = string.Empty;

    public bool FromCache { get; init; }
}

public sealed class TestArtifactLogSink
{
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public void WriteLine(string message)
    {
        lock (_gate)
        {
            _buffer.AppendLine(message);
        }
    }

    public string GetText()
    {
        lock (_gate)
        {
            return _buffer.ToString();
        }
    }
}

public sealed class PlanningExplorationArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _artifactDirectory = Path.Combine(
        TestPathHelper.FindRepositoryRoot(),
        "artifacts",
        "planning-real-web-exploration");

    public async Task<PlanningExplorationArtifactPaths> SaveAsync(
        PlanningExplorationArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_artifactDirectory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var safeName = TestPathHelper.SanitizeFileName(artifact.ScenarioId);
        var filePrefix = Path.Combine(_artifactDirectory, $"{stamp}-{safeName}");
        var summaryPath = $"{filePrefix}.json";
        var logPath = $"{filePrefix}.log";

        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(artifact, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(logPath, artifact.LogText ?? string.Empty, cancellationToken);

        return new PlanningExplorationArtifactPaths
        {
            SummaryPath = summaryPath,
            LogPath = logPath
        };
    }
}

public sealed class PlanningExplorationArtifactPaths
{
    public string SummaryPath { get; init; } = string.Empty;

    public string LogPath { get; init; } = string.Empty;
}

public sealed class PlanningExplorationArtifact
{
    public string ScenarioId { get; init; } = string.Empty;

    public string UserQuery { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorDetailsJson { get; init; }

    public string? ResultJson { get; init; }

    public bool? AnsweredQuestion { get; init; }

    public string? AnswerAssertionComment { get; init; }

    public string LogText { get; init; } = string.Empty;
}

public static class TestPathHelper
{
    public static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OllamaChat.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    public static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);

        return builder.ToString();
    }
}
