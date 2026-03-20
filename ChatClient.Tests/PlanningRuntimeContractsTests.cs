using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Headers;
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
                    Llm = "synthesizer",
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
                    Tool = SearchToolName,
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
                    Tool = SearchToolName,
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
    public async Task PlanningSessionService_StartAsync_RequiresEnabledTools()
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
                    Llm = "synthesizer",
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => BuiltInWebToolLogic.SearchAsync(
            httpClientFactory.Object,
            NullLogger.Instance,
            new WebSearchInput("item")));

        Assert.Contains("structured candidate results", exception.Message, StringComparison.Ordinal);
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
        Assert.Equal("https://example.com/item-a", item.Url);
        Assert.Equal("Item A title", item.Title);
        Assert.Equal("Item A summary.", item.Snippet);
        Assert.Equal("example.com", item.SiteName);
        Assert.Equal("example.com/item-a", item.DisplayUrl);
    }

    [Fact]
    public async Task BuiltInWebSearchLogic_RetriesAfterRateLimit()
    {
        BuiltInWebToolLogic.ResetSearchStateForTests(CreateTempSearchCacheDirectory());

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
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html)
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
        Assert.Single(result.Results);
        Assert.Equal("https://example.com/item-a", result.Results[0].Url);
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
                        ["url"] = Ref("$searchPages.results[].url", mode: "map")
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
                        ["page"] = Ref("$searchPages.results", mode: "map")
                    }
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.True(trace.Success));
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "searchAgain",
                    Tool = SearchToolName,
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "shortlistPackages",
                    Llm = "shortlist",
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "extractPackage",
                    Llm = "extractor",
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
                    }, aggregate: PlanStepOutputAggregates.Collect)
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "extractPackage",
                    Llm = "extractor",
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
                    }, aggregate: PlanStepOutputAggregates.Collect)
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan, [CreateStaticSearchDescriptor()]));

        Assert.Contains("declares type 'array<object>'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("produces object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_DescribesNestedCollectedArrayShape_WhenDownstreamBindingTypeMismatches()
    {
        var plan = new PlanDefinition
        {
            Goal = "Collect mapped arrays, then bind them as a flat array.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "extractPackages",
                    Llm = "extractor",
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
                    }, aggregate: PlanStepOutputAggregates.Collect)
                },
                new PlanStep
                {
                    Id = "reviewPackages",
                    Llm = "review",
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

        Assert.False(PlanValidator.TryValidate(plan, [CreateStaticSearchDescriptor()], out var issue));
        Assert.NotNull(issue);
        Assert.Equal("binding_type_mismatch", issue!.Code);
        Assert.Equal("array<array<object>>", issue.Actual);
        Assert.Contains("array<array<object>>", issue.Message, StringComparison.Ordinal);
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("maze generator nuget")
                    }
                },
                new PlanStep
                {
                    Id = "shortlistPackages",
                    Llm = "shortlist",
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
                    Tool = "mock-web:opaque",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["seed"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "searchAgain",
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = Ref("$opaqueSource.payload")
                    }
                }
            ]
        };

        PlanValidator.ValidateOrThrow(plan, [opaqueTool, CreateStaticSearchDescriptor()]);

        var result = await executor.ExecuteAsync(plan);
        var trace = Assert.Single(result.StepTraces, trace => !trace.Success);
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("many")
                    }
                },
                new PlanStep
                {
                    Id = "singleResult",
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("single")
                    }
                },
                new PlanStep
                {
                    Id = "pairResults",
                    Tool = PairToolName,
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
    public void PlanValidator_RejectsJsonLlmStepWithoutOutputSchema()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract a product summary.",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Llm = "extractor",
                    SystemPrompt = "Return JSON only.",
                    UserPrompt = "Extract the requested fields.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["content"] = JsonValue.Create("example")
                    },
                    Out = new PlanStepOutputContract
                    {
                        Format = PlanStepOutputFormats.Json,
                        Aggregate = PlanStepOutputAggregates.Single
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must provide out.schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanValidator_RejectsMappedLlmStepWithSingleAggregate()
    {
        var plan = new PlanDefinition
        {
            Goal = "Extract package facts.",
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
                    Id = "extractFacts",
                    Llm = "extractor",
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
                        aggregate: PlanStepOutputAggregates.Single)
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("out.aggregate='collect' or 'flatten'", exception.Message, StringComparison.Ordinal);
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
                    Llm = "extractor",
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
                    Tool = SearchToolName,
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "extractFacts",
                    Llm = "extractor",
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
                        aggregate: PlanStepOutputAggregates.Flatten)
                }
            ]
        };

        var result = await executor.ExecuteAsync(plan);

        Assert.All(result.StepTraces, trace => Assert.True(trace.Success));
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
                    Llm = "extractor",
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
        Assert.False(trace.Success);
        Assert.Equal("output_contract_failed", trace.ErrorCode);
        Assert.Contains("declared output contract", trace.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
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
                    Tool = "mock-web:opaque",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["seed"] = JsonValue.Create("example")
                    }
                },
                new PlanStep
                {
                    Id = "extractFacts",
                    Llm = "extractor",
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
        var trace = Assert.Single(result.StepTraces, candidate => !candidate.Success);
        Assert.Equal("extractFacts", trace.StepId);
        Assert.Equal("llm_input_contract_failed", trace.ErrorCode);
        Assert.Contains("declared binding types", trace.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("expected 'array<object>'", trace.ErrorDetails?.GetProperty("issues")[0].GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentStepRunner_BuildExecutionContract_ClarifiesCollectPerCallSemantics()
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
            Aggregate: PlanStepOutputAggregates.Collect,
            CallSchema: callSchema,
            FinalSchema: finalSchema,
            IsExplicit: true));

        Assert.Contains("runtime collects those per-call values into the final array", prompt, StringComparison.Ordinal);
        Assert.Contains("schema below describes the single-call value, not the final collected array", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not wrap the value in an extra array", prompt, StringComparison.Ordinal);
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
                    Llm = "extractor",
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
        Assert.True(trace.Success);
        Assert.Null(trace.ErrorCode);
    }

    private static PlanningSessionService CreateSessionService()
    {
        var chatClientFactory = new Mock<IPlanningChatClientFactory>();
        var appToolCatalog = new Mock<IAppToolCatalog>();
        var mcpUserInteractionService = new Mock<IMcpUserInteractionService>();
        appToolCatalog
            .Setup(catalog => catalog.ListToolsAsync(It.IsAny<McpClientRequestContext?>(), It.IsAny<CancellationToken>()))
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

    private static PlanStepOutputContract JsonOut(JsonObject schema, string aggregate = PlanStepOutputAggregates.Single) =>
        new()
        {
            Format = PlanStepOutputFormats.Json,
            Aggregate = aggregate,
            Schema = JsonSerializer.SerializeToElement(schema)
        };

    private static PlanStepOutputContract StringOut(string aggregate = PlanStepOutputAggregates.Single) =>
        new()
        {
            Format = PlanStepOutputFormats.String,
            Aggregate = aggregate
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
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("LLM execution is not expected in this test.");
    }

    private sealed class DelegateAgentStepRunner(Func<PlanStep, JsonElement, ResultEnvelope<JsonElement?>> execute) : IAgentStepRunner
    {
        public Task<ResultEnvelope<JsonElement?>> ExecuteAsync(PlanStep step, JsonElement resolvedInputs, CancellationToken cancellationToken = default) =>
            Task.FromResult(execute(step, resolvedInputs));
    }
}
