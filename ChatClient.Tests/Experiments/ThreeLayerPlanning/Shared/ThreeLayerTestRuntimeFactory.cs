using System.ClientModel;
using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public static class ThreeLayerTestRuntimeFactory
{
    private const string OpenAiServerName = "OpenAI";
    private const string OpenAiModel = "gpt-5.4-mini";

    public static IExperimentLlmClient CreateLiveLlmClient() =>
        new ChatClientExperimentLlmClient(BuildChatClient());

    public static IChatClient BuildChatClient()
    {
        var server = LoadOpenAiServer();
        var configuration = BuildConfiguration();
        var apiKey = LlmServerConfigHelper.GetConfiguredOpenAiApiKey(configuration, server);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key is required for three-layer live tests. Configure OpenAI:ApiKey in user-secrets or environment variables.");

        var normalizedBaseUrl = LlmServerConfigHelper.GetNormalizedOpenAiBaseUrl(server, LlmServerConfig.DefaultOpenAiUrl);
        var endpoint = new Uri(normalizedBaseUrl.EndsWith("/", StringComparison.Ordinal) ? normalizedBaseUrl : $"{normalizedBaseUrl}/");
        var clientOptions = LlmServerConfigHelper.CreateOpenAIClientOptions(server, endpoint);

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(OpenAiModel)
            .AsIChatClient();
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddUserSecrets(typeof(LlmServerConfigHelper).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

    private static LlmServerConfig LoadOpenAiServer()
    {
        var serversPath = Path.Combine(ResolveRepositoryRoot(), "ChatClient.Api", "Data", "llm_servers.json");
        var json = File.ReadAllText(serversPath);
        var servers = JsonSerializer.Deserialize<List<LlmServerConfig>>(json, ExperimentJson.SerializerOptions)
            ?? throw new InvalidOperationException($"Could not deserialize LLM server configs from '{serversPath}'.");

        return servers.FirstOrDefault(server =>
                   server.ServerType == ServerType.ChatGpt &&
                   string.Equals(server.Name, OpenAiServerName, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"OpenAI server '{OpenAiServerName}' was not found in '{serversPath}'.");
    }

    private static string ResolveRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    public static IReadOnlyCollection<AppToolDescriptor> CreateRealWebTools(IHttpClientFactory httpClientFactory) =>
        [
            CreateDescriptor(
                serverName: "built-in-web",
                toolName: "search",
                description: "Search the web with required 'query' and optional 'limit', then return structured candidate page references under 'results'. Each search.results[] item is directly compatible with the download tool's 'page' input. Results are candidate pages, not verified entities.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" },
                        "limit": { "type": "integer", "minimum": 1, "maximum": 10 }
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
                              "provider": { "type": ["string", "null"] },
                              "url": { "type": "string" },
                              "title": { "type": "string" },
                              "snippet": { "type": ["string", "null"] }
                            },
                            "required": ["url", "title"]
                          }
                        }
                      },
                      "required": ["query", "results"]
                    }
                    """,
                execute: async arguments => await BuiltInWebToolLogic.SearchAsync(
                    httpClientFactory,
                    NullLogger.Instance,
                    new WebSearchInput(
                        Query: TryGetString(arguments, "query") ?? throw new InvalidOperationException("query is required."),
                        Limit: TryGetInt(arguments, "limit")))),
            CreateDescriptor(
                serverName: "built-in-web",
                toolName: "download",
                description: "Download a single web page from a search result object or raw URL and return structured full-page content.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "page": {
                          "type": "object",
                          "properties": {
                            "url": { "type": "string" },
                            "title": { "type": ["string", "null"] },
                            "snippet": { "type": ["string", "null"] }
                          },
                          "required": ["url"]
                        },
                        "url": { "type": "string" }
                      },
                      "oneOf": [{ "required": ["page"] }, { "required": ["url"] }]
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
                execute: async arguments => await BuiltInWebToolLogic.DownloadAsync(
                    httpClientFactory,
                    NullLogger.Instance,
                    new WebDownloadInput(
                        Page: TryGetObject(arguments, "page") is { } page
                            ? new WebDownloadPageRef(
                                Url: TryGetString(page, "url") ?? string.Empty,
                                Title: TryGetString(page, "title"),
                                Snippet: TryGetString(page, "snippet"),
                                SiteName: null,
                                DisplayUrl: null,
                                Age: null,
                                ThumbnailUrl: null,
                                Position: null)
                            : null,
                        Url: TryGetString(arguments, "url"))))
        ];

    public static IReadOnlyCollection<AppToolDescriptor> CreateMockWebTools() =>
        [
            CreateDescriptor(
                serverName: "mock-web",
                toolName: "search",
                description: "Return mock candidate product pages.",
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
                        "results": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "properties": {
                              "url": { "type": "string" },
                              "title": { "type": "string" },
                              "provider": { "type": "string" }
                            },
                            "required": ["url", "title", "provider"]
                          }
                        }
                      },
                      "required": ["results"]
                    }
                    """,
                execute: _ => Task.FromResult<object>(new
                {
                    results = new[]
                    {
                        new { url = "https://example.test/vacuum-a", title = "Vacuum A", provider = "mock" },
                        new { url = "https://example.test/vacuum-b", title = "Vacuum B", provider = "mock" }
                    }
                })),
            CreateDescriptor(
                serverName: "mock-web",
                toolName: "download",
                description: "Return mock downloaded product pages.",
                inputSchemaJson: """
                    {
                      "type": "object",
                      "properties": {
                        "page": {
                          "type": "object",
                          "properties": {
                            "url": { "type": "string" }
                          },
                          "required": ["url"]
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
                    var page = TryGetObject(arguments, "page") ?? throw new InvalidOperationException("page is required.");
                    var url = TryGetString(page, "url") ?? string.Empty;
                    var payload = url.Contains("vacuum-a", StringComparison.OrdinalIgnoreCase)
                        ? new
                        {
                            url,
                            title = "Vacuum A",
                            content = "Vacuum A costs 549 EUR, has mop support, and is a balanced option."
                        }
                        : new
                        {
                            url,
                            title = "Vacuum B",
                            content = "Vacuum B costs 699 EUR, has mop support, and is more expensive."
                        };
                    return Task.FromResult<object>(payload);
                })
        ];

    private static AppToolDescriptor CreateDescriptor(
        string serverName,
        string toolName,
        string description,
        string inputSchemaJson,
        string outputSchemaJson,
        Func<Dictionary<string, object?>, Task<object>> execute)
    {
        var inputSchema = ParseJsonElement(inputSchemaJson);
        var outputSchema = ParseJsonElement(outputSchemaJson);

        return new AppToolDescriptor(
            QualifiedName: $"{serverName}:{toolName}",
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: description,
            InputSchema: inputSchema,
            OutputSchema: outputSchema,
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: (arguments, cancellationToken) => execute(arguments),
            PlanningMetadata: new AppToolPlanningMetadata(
                Purpose: description,
                Constraints: toolName.Equals("search", StringComparison.OrdinalIgnoreCase)
                    ? "Produces candidate references, not verified facts."
                    : "Produces source documents.",
                Limits: toolName.Equals("search", StringComparison.OrdinalIgnoreCase) ? "limit <= 10" : null,
                PlannerRole: toolName.Equals("search", StringComparison.OrdinalIgnoreCase) ? AppToolPlannerRole.Discover : AppToolPlannerRole.Acquire,
                ProducesKind: toolName.Equals("search", StringComparison.OrdinalIgnoreCase) ? AppToolProducesKind.Reference : AppToolProducesKind.Document));
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? TryGetString(Dictionary<string, object?> source, string propertyName) =>
        source.TryGetValue(propertyName, out var value) && value is string text
            ? text
            : null;

    private static int? TryGetInt(Dictionary<string, object?> source, string propertyName) =>
        source.TryGetValue(propertyName, out var value)
            ? value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                _ => null
            }
            : null;

    private static Dictionary<string, object?>? TryGetObject(Dictionary<string, object?> source, string propertyName) =>
        source.TryGetValue(propertyName, out var value) && value is Dictionary<string, object?> objectValue
            ? objectValue
            : null;
}
