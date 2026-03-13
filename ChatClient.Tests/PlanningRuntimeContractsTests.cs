using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public class PlanningRuntimeContractsTests
{
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
                    Tool = "search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("best robot vacuum")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Llm = "synthesizer",
                    SystemPrompt = "Use $searchPages[] to answer the user.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = JsonValue.Create("$searchPages[]")
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
        service.State.FinalResult = ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new { ok = true }));
        service.State.Events.Add(new DiagnosticPlanRunEvent("test", "event"));
        service.State.LogLines.Add("log");
        service.State.AvailableTools.Add(new PlanningToolOption
        {
            Name = "search",
            DisplayName = "Web Search",
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
    public void WebSearchTool_PlannerMetadata_WarnsThatResultsNeedRelevanceCheck()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var tool = new WebSearchTool(httpClientFactory.Object, NullLogger<WebSearchTool>.Instance);

        Assert.Contains("raw structured search results", tool.PlannerMetadata.Description, StringComparison.Ordinal);
        Assert.Contains("checked for relevance", tool.PlannerMetadata.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void WebDownloadTool_PlannerMetadata_DescribesPagePreservingContentContract()
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var tool = new WebDownloadTool(httpClientFactory.Object, NullLogger<WebDownloadTool>.Instance);

        Assert.Contains("full search-result object", tool.PlannerMetadata.Description, StringComparison.Ordinal);
        Assert.Contains("'content'", tool.PlannerMetadata.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSearchTool_ExtractsStructuredResultsFromSearchMarkup()
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

        var tool = new WebSearchTool(httpClientFactory.Object, NullLogger<WebSearchTool>.Instance);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "item a" }));

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);

        var items = JsonSerializer.Deserialize<List<WebSearchResult>>(result.Data.Value.GetRawText());
        var item = Assert.Single(items!);
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
    public async Task WebSearchTool_Fails_WhenStructuredMarkupIsMissing()
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

        var tool = new WebSearchTool(httpClientFactory.Object, NullLogger<WebSearchTool>.Instance);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { query = "item" }));

        Assert.False(result.Ok);
        Assert.Equal("search_failed", result.Error?.Code);
    }

    [Fact]
    public async Task WebDownloadTool_PreservesPageObjectAndAddsContent()
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

        var tool = new WebDownloadTool(httpClientFactory.Object, NullLogger<WebDownloadTool>.Instance);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            page = new
            {
                url = "https://example.com/item-a",
                title = "Search title",
                snippet = "Search snippet",
                siteName = "Example"
            }
        }));

        Assert.True(result.Ok);
        Assert.NotNull(result.Data);

        var payload = JsonSerializer.Deserialize<JsonElement>(result.Data.Value.GetRawText());
        Assert.Equal("https://example.com/item-a", payload.GetProperty("url").GetString());
        Assert.Equal("Search title", payload.GetProperty("title").GetString());
        Assert.Equal("Search snippet", payload.GetProperty("snippet").GetString());
        Assert.Equal("Example", payload.GetProperty("siteName").GetString());
        Assert.Equal("Downloaded page title Example body text.", payload.GetProperty("content").GetString());
    }

    [Fact]
    public async Task PlanExecutor_AutoProjectsUrlFieldWhenToolInputExpectsScalar()
    {
        var searchTool = new StaticSearchResultsTool();
        var downloadTool = new RecordingDownloadTool();
        var executor = new PlanExecutor(new ToolRegistry([searchTool, downloadTool]), new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Download search results.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Tool = "search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Tool = "download",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("$searchPages[]")
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
        var searchTool = new StaticSearchResultsTool();
        var downloadTool = new PageRecordingDownloadTool();
        var executor = new PlanExecutor(new ToolRegistry([searchTool, downloadTool]), new ThrowingAgentStepRunner());
        var plan = new PlanDefinition
        {
            Goal = "Download full search result objects.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Tool = "search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "downloadPages",
                    Tool = "download",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["page"] = JsonValue.Create("$searchPages[]")
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
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var searchTool = new WebSearchTool(httpClientFactory.Object, NullLogger<WebSearchTool>.Instance);
        var downloadTool = new WebDownloadTool(httpClientFactory.Object, NullLogger<WebDownloadTool>.Instance);

        return new PlanningSessionService(
            chatClientFactory.Object,
            searchTool,
            downloadTool,
            NullLogger<PlanningSessionService>.Instance);
    }

    private sealed class StubHttpMessageHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(html)
            });
    }

    private sealed class StaticSearchResultsTool : ITool
    {
        public string Name => "search";

        public ToolPlannerMetadata PlannerMetadata => new(
            "search",
            "Structured search results.",
            JsonNode.Parse(@"{""type"":""object"",""properties"":{""query"":{""type"":""string""}},""required"":[""query""]}")!.AsObject(),
            JsonNode.Parse(@"{""type"":""array"",""items"":{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""}},""required"":[""url"",""title""]}}")!.AsObject(),
            [],
            []);

        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default) =>
            Task.FromResult(ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new[]
            {
                new { url = "https://example.com/item-a", title = "Item A" },
                new { url = "https://example.com/item-b", title = "Item B" }
            })));
    }

    private sealed class RecordingDownloadTool : ITool
    {
        public List<string> ReceivedUrls { get; } = [];

        public string Name => "download";

        public ToolPlannerMetadata PlannerMetadata => new(
            "download",
            "Download by URL.",
            JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""}},""required"":[""url""]}")!.AsObject(),
            JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""}},""required"":[""url""]}")!.AsObject(),
            [],
            []);

        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default)
        {
            var url = input.GetProperty("url").GetString() ?? string.Empty;
            ReceivedUrls.Add(url);
            return Task.FromResult(ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new { url })));
        }
    }

    private sealed class PageRecordingDownloadTool : ITool
    {
        public List<string> ReceivedTitles { get; } = [];

        public string Name => "download";

        public ToolPlannerMetadata PlannerMetadata => new(
            "download",
            "Download by page object.",
            JsonNode.Parse(@"{""type"":""object"",""properties"":{""page"":{""type"":""object""}}}")!.AsObject(),
            JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""},""content"":{""type"":""string""}},""required"":[""url"",""title"",""content""]}")!.AsObject(),
            [],
            []);

        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default)
        {
            var page = input.GetProperty("page");
            ReceivedTitles.Add(page.GetProperty("title").GetString() ?? string.Empty);
            return Task.FromResult(ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new
            {
                url = page.GetProperty("url").GetString(),
                title = page.GetProperty("title").GetString(),
                content = "content"
            })));
        }
    }

    private sealed class ThrowingAgentStepRunner : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("LLM execution is not expected in this test.");
    }
}
