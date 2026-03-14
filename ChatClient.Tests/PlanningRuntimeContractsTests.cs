using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public class PlanningRuntimeContractsTests
{
    private const string SearchToolName = "mock-web:search";
    private const string DownloadToolName = "mock-web:download";

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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("best robot vacuum")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Llm = "synthesizer",
                    SystemPrompt = "Use $searchPages.results to answer the user.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = JsonValue.Create("$searchPages.results")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must not embed step refs inside systemPrompt", exception.Message, StringComparison.Ordinal);
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
                    Llm = "synthesizer",
                    SystemPrompt = "Summarize the selected model {model_name}. Return JSON only.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["modelName"] = JsonValue.Create("Eufy X10 Pro Omni")
                    }
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
                    Llm = "synthesizer",
                    SystemPrompt = "Summarize the selected model. Return JSON only.",
                    UserPrompt = "Compare [[model_a]] against [[model_b]].",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["modelA"] = JsonValue.Create("Eufy X10 Pro Omni"),
                        ["modelB"] = JsonValue.Create("Roborock Qrevo Curv")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must not contain unresolved template placeholders", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningSessionService_StartAsync_RequiresEnabledTools()
    {
        var service = CreateSessionService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(new PlanningRunRequest
        {
            Model = new ServerModel(Guid.NewGuid(), "model-a"),
            UserQuery = "Compare two products",
            EnabledToolNames = []
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
                    Llm = "synthesizer",
                    SystemPrompt = "sys",
                    UserPrompt = "user",
                    In = []
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
    public async Task BuiltInWebSearchLogic_Fails_WhenStructuredMarkupIsMissing()
    {
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item")));

        Assert.Equal("Search returned no structured candidate results.", exception.Message);
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
            new WebDownloadInput(new WebSearchResult(
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
    public async Task PlanExecutor_AutoProjectsUrlFieldWhenToolInputExpectsScalar()
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Tool = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("$searchPages.results")
                    }
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.True(trace.Success));
        Assert.Equal(
            ["https://example.com/item-a", "https://example.com/item-b"],
            downloadTool.ReceivedUrls);
    }

    [Fact]
    public async Task PlanExecutor_FansOutWholeSearchObjects_WhenToolInputUsesPage()
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Tool = DownloadToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = JsonValue.Create("$searchPages.results")
                    }
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.True(trace.Success));
        Assert.Equal(["Item A", "Item B"], downloadTool.ReceivedTitles);
    }

    private static PlanningSessionService CreateSessionService()
    {
        var chatClientFactory = new Mock<IPlanningChatClientFactory>();
        var appToolCatalog = new Mock<IAppToolCatalog>();
        var mcpUserInteractionService = new Mock<IMcpUserInteractionService>();
        appToolCatalog
            .Setup(catalog => catalog.ListToolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AppToolDescriptor>());

        return new PlanningSessionService(
            chatClientFactory.Object,
            appToolCatalog.Object,
            mcpUserInteractionService.Object,
            NullLogger<PlanningSessionService>.Instance);
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

    private sealed class ThrowingAgentStepRunner : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("LLM execution is not expected in this test.");
    }
}
