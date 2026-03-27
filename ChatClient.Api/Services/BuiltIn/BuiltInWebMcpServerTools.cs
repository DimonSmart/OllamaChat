using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInWebMcpServerTools
{
    private const string SearchDescription =
        "Search the web with required 'query' and optional 'limit', then return structured candidate page references under 'results'. 'limit' is only a maximum, not a guarantee: the tool may return fewer results, and some results may be partially relevant. For broader coverage, especially when many distinct entities are needed, prefer multiple searches with different phrasings and/or a higher 'limit'. Each result includes a URL, title, provider, and optional search metadata such as thumbnailUrl. Each search.results[] item is directly compatible with the download tool's 'page' input, so downstream plans can pass page objects without projecting to '.url' first. Results are candidate pages, not verified entities. On failure, the tool returns a structured error payload with code, provider, providerAttempts, query, retryability, fallback usage, and technical diagnostics.";
    private const string DownloadDescription =
        "Download a single web page to obtain full source content from a candidate reference or raw URL. Provide exactly one of 'page' or 'url'. Use this when titles, snippets, rankings, or other lightweight records are not enough and you need exact facts, quotes, specs, or detailed extraction from the underlying page. Prefer 'page' when you already have a search result object: each search.results[] item is directly compatible with 'page' and preserves search metadata such as title, provider, snippet, or thumbnailUrl. Use 'url' only when you have a raw absolute URL string. To download multiple search results, bind 'page' from search.results with mode=map so the step runs once per result. On failure, the tool returns a structured error payload with code, host, retryability, and technical diagnostics.";
    private static readonly JsonElement SearchInputSchema = ParseJsonElement(
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Search query to submit to the search engine."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 10,
              "description": "Maximum number of results requested, not guaranteed. Search may return fewer results. Default 8, max 10."
            }
          },
          "required": ["query"]
        }
        """);
    private static readonly JsonElement SearchOutputSchema = ParseJsonElement(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string" },
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "provider": { "type": "string" },
                  "url": { "type": "string" },
                  "title": { "type": "string" },
                  "snippet": { "type": ["string", "null"] },
                  "siteName": { "type": ["string", "null"] },
                  "displayUrl": { "type": ["string", "null"] },
                  "age": { "type": ["string", "null"] },
                  "thumbnailUrl": { "type": ["string", "null"] },
                  "position": { "type": ["integer", "null"] }
                },
                "required": ["provider", "url", "title"]
              }
            }
          },
          "required": ["query", "results"]
        }
        """);
    private static readonly JsonElement DownloadInputSchema = ParseJsonElement(
        """
        {
          "type": "object",
          "properties": {
            "page": {
              "type": "object",
              "description": "Page-reference object for acquiring full source content. Prefer passing one search.results[] item directly here; for multiple results, bind page from search.results with mode=map.",
              "properties": {
                "url": { "type": "string" },
                "title": { "type": ["string", "null"] },
                "provider": { "type": ["string", "null"] },
                "snippet": { "type": ["string", "null"] },
                "siteName": { "type": ["string", "null"] },
                "displayUrl": { "type": ["string", "null"] },
                "age": { "type": ["string", "null"] },
                "thumbnailUrl": { "type": ["string", "null"] },
                "position": { "type": ["integer", "null"] }
              },
              "required": ["url"]
            },
            "url": {
              "type": "string",
              "description": "Raw absolute URL. Use this only when a full page object is not available."
            }
          },
          "oneOf": [
            { "required": ["page"] },
            { "required": ["url"] }
          ]
        }
        """);
    private static readonly JsonElement DownloadOutputSchema = ParseJsonElement(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string" },
            "title": { "type": "string" },
            "content": { "type": "string" },
            "provider": { "type": ["string", "null"] },
            "snippet": { "type": ["string", "null"] },
            "siteName": { "type": ["string", "null"] },
            "displayUrl": { "type": ["string", "null"] },
            "age": { "type": ["string", "null"] },
            "thumbnailUrl": { "type": ["string", "null"] },
            "position": { "type": ["integer", "null"] }
          },
          "required": ["url", "title", "content"]
        }
        """);

    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("92b6b4b0-0d4f-4e7f-a6bc-8cd2c4d6622e"),
        key: "built-in-web",
        name: "Built-in Web MCP Server",
        description: "Searches the web and downloads web pages with structured results.",
        registerTools: static builder => builder.WithTools(CreateTools()));

    private static IEnumerable<McpServerTool> CreateTools()
    {
        var invokerType = typeof(ToolInvoker);
        var searchTool = McpServerTool.Create(
            invokerType.GetMethod(nameof(ToolInvoker.SearchAsync), BindingFlags.Instance | BindingFlags.Public)!
            ,
            CreateInvoker,
            new McpServerToolCreateOptions
            {
                Name = "search",
                Description = SearchDescription,
                ReadOnly = true,
                OpenWorld = true,
                UseStructuredContent = true
            });
        searchTool.ProtocolTool.InputSchema = SearchInputSchema.Clone();
        searchTool.ProtocolTool.OutputSchema = SearchOutputSchema.Clone();

        var downloadTool = McpServerTool.Create(
            invokerType.GetMethod(nameof(ToolInvoker.DownloadAsync), BindingFlags.Instance | BindingFlags.Public)!
            ,
            CreateInvoker,
            new McpServerToolCreateOptions
            {
                Name = "download",
                Description = DownloadDescription,
                ReadOnly = true,
                OpenWorld = true,
                UseStructuredContent = true
            });
        downloadTool.ProtocolTool.InputSchema = DownloadInputSchema.Clone();
        downloadTool.ProtocolTool.OutputSchema = DownloadOutputSchema.Clone();

        return [searchTool, downloadTool];
    }

    private static object CreateInvoker(RequestContext<CallToolRequestParams> request)
    {
        var services = request.Services;
        ArgumentNullException.ThrowIfNull(services);
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var logger = services.GetRequiredService<ILogger<BuiltInWebMcpServerTools>>();
        return new ToolInvoker(httpClientFactory, logger);
    }

    [McpServerTool(Name = "search", ReadOnly = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(SearchDescription)]
    public static async Task<object> SearchAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Search query to submit to the search engine.")] string query,
        [Description("Maximum number of results to return. Default 8, max 10.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await BuiltInWebToolLogic.SearchAsync(
                httpClientFactory,
                logger,
                new WebSearchInput(query, limit),
                cancellationToken);
        }
        catch (WebToolException ex)
        {
            return CreateKnownError(ex);
        }
    }

    [McpServerTool(Name = "download", ReadOnly = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(DownloadDescription)]
    public static async Task<object> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Page-reference object for acquiring full source content. Must contain at least 'url'; other metadata such as title and snippet are optional.")] WebDownloadPageRef? page = null,
        [Description("Absolute HTTP or HTTPS URL to download when a full page object is not available. Use this when you need the full page content rather than lightweight metadata.")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await BuiltInWebToolLogic.DownloadAsync(
                httpClientFactory,
                logger,
                new WebDownloadInput(page, url),
                cancellationToken);
        }
        catch (WebToolException ex)
        {
            return CreateKnownError(ex);
        }
    }

    private static CallToolResult CreateKnownError(WebToolException exception) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = exception.Message
                }
            ],
            StructuredContent = System.Text.Json.JsonSerializer.SerializeToNode(new
            {
                code = exception.Code,
                message = exception.Message,
                status = exception.Details.Status,
                needsReplan = exception.Details.NeedsReplan,
                type = exception.Details.Type,
                details = exception.Details.Details,
                query = exception.Details.Query,
                provider = exception.Details.Provider,
                providerAttempts = exception.Details.ProviderAttempts,
                fallbackTried = exception.Details.FallbackTried,
                operation = exception.Details.Operation,
                url = exception.Details.Url,
                host = exception.Details.Host,
                retryable = exception.Details.Retryable,
                httpStatusCode = exception.Details.HttpStatusCode,
                technicalMessage = exception.Details.TechnicalMessage,
                exceptionType = exception.Details.ExceptionType
            })
        };

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public sealed class ToolInvoker(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger)
    {
        [Description(SearchDescription)]
        public async Task<CallToolResult> SearchAsync(
            [Description("Search query to submit to the search engine.")] string query,
            [Description("Maximum number of results to return. Default 8, max 10.")] int? limit = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await BuiltInWebToolLogic.SearchAsync(
                    httpClientFactory,
                    logger,
                    new WebSearchInput(query, limit),
                    cancellationToken);

                return CreateSuccessResult(result);
            }
            catch (WebToolException ex)
            {
                return CreateKnownError(ex);
            }
        }

        [Description(DownloadDescription)]
        public async Task<CallToolResult> DownloadAsync(
            [Description("Page-reference object. Must contain at least 'url'; other metadata such as title and snippet are optional.")] WebDownloadPageRef? page = null,
            [Description("Absolute HTTP or HTTPS URL to download when a full page object is not available.")] string? url = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await BuiltInWebToolLogic.DownloadAsync(
                    httpClientFactory,
                    logger,
                    new WebDownloadInput(page, url),
                    cancellationToken);

                return new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToNode(result)
                };
            }
            catch (WebToolException ex)
            {
                return CreateKnownError(ex);
            }
        }
    }

    private static CallToolResult CreateSuccessResult<T>(T result) =>
        new()
        {
            StructuredContent = JsonSerializer.SerializeToNode(result)
        };
}
