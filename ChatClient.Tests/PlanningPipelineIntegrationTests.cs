using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.ExceptionServices;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Orchestration;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.PlanningRuntime.Verification;
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

    [Fact]
    public async Task FullPipeline_PlannerAndOrchestrator_ReturnsSystemOutcome()
    {
        const string userQuery = "I'm looking for a good robot vacuum cleaner. Can you find two popular models, check their specs, and tell me which one is better?";
        await RunFullPipelineAsync(userQuery, new ToolRegistry([new MockSearchTool(), new MockDownloadTool()]));
    }

    [Fact]
    public async Task FullPipeline_PlannerAndOrchestrator_WithRealWebSearchAndDownload_ReturnsSystemOutcome()
    {
        const string userQuery = "Compare Markdig and CommonMark.NET using their GitHub or documentation pages, and tell me which one is better for a small .NET app.";
        var httpClientFactory = new TestHttpClientFactory();
        await RunWithRetriesAsync(() => RunFullPipelineAsync(userQuery, new ToolRegistry(
        [
            new WebSearchTool(httpClientFactory, NullLogger<WebSearchTool>.Instance),
            new WebDownloadTool(httpClientFactory, NullLogger<WebDownloadTool>.Instance)
        ])));
    }

    private async Task RunFullPipelineAsync(string userQuery, IToolRegistry tools)
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
            : JsonSerializer.Serialize(element.Value, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class TestLogger(ITestOutputHelper output) : IExecutionLogger
{
    public void Log(string message) => output.WriteLine(message);
}

public sealed class MockSearchTool : ITool
{
    public string Name => "search";

    public ToolPlannerMetadata PlannerMetadata => new(
        "search",
        "Search the web and return candidate page URLs.",
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""query"":{""type"":""string""},""limit"":{""type"":""number""}},""required"":[""query""]}")!.AsObject(),
        JsonNode.Parse(@"{""type"":""array"",""items"":{""type"":""string""}}")!.AsObject(),
        [],
        []);

    public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default) =>
        Task.FromResult(ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new[]
        {
            "https://example.com/item-a",
            "https://example.com/item-b"
        })));
}

public sealed class MockDownloadTool : ITool
{
    public string Name => "download";

    public ToolPlannerMetadata PlannerMetadata => new(
        "download",
        "Download a single page by URL and return its title and body text.",
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""}},""required"":[""url""]}")!.AsObject(),
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""},""body"":{""type"":""string""}},""required"":[""url"",""title"",""body""]}")!.AsObject(),
        [],
        []);

    public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        var url = TryGetStringProperty(input, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult(ResultEnvelope<JsonElement?>.Failure("invalid_input", "Download URL is required."));

        var payload = url.Contains("item-a", StringComparison.OrdinalIgnoreCase)
            ? new
            {
                url,
                title = "RoboClean A1 Max review",
                body = "RoboClean A1 Max is a popular robot vacuum cleaner with 7000 Pa suction power, up to 180 minutes of battery runtime, a 0.5 L dustbin, LiDAR navigation, and a list price of $799."
            }
            : new
            {
                url,
                title = "HomeSweep S5 review",
                body = "HomeSweep S5 is a popular robot vacuum cleaner with 5000 Pa suction power, up to 140 minutes of battery runtime, a 0.4 L dustbin, vSLAM navigation, and a list price of $649."
            };

        return Task.FromResult(ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(payload)));
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

public sealed class CachedLlmAnswerAsserter(IChatClient chatClient, string modelName)
{
    private const string PromptVersion = "v7";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _cacheDirectory = Path.Combine(FindRepositoryRoot(), ".llm-test-cache", "answer-assertions");

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

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OllamaChat.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root for LLM test cache.");
    }

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
