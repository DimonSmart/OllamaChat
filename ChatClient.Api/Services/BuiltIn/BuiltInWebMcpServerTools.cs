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
        "Search the web and return structured candidate page references under 'results'. Each result includes a URL and title plus optional search metadata. Results are candidate pages, not verified entities.";
    private const string DownloadDescription =
        "Download a single web page. Provide exactly one of 'page' or 'url'. 'page' is a page-reference object that must contain at least 'url' and may also carry optional metadata such as title or snippet; the tool returns that metadata together with downloaded 'content'. If only a raw absolute URL is available, pass it via 'url'. On failure, the tool returns a structured error payload with code, host, retryability, and technical diagnostics.";
    private static readonly JsonElement DownloadInputSchema = ParseJsonElement(
        """
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
        """);
    private static readonly JsonElement DownloadOutputSchema = ParseJsonElement(
        """
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
    public static Task<WebSearchData> SearchAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Search query to submit to the search engine.")] string query,
        [Description("Maximum number of results to return. Default 4, max 6.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return BuiltInWebToolLogic.SearchAsync(
            httpClientFactory,
            logger,
            new WebSearchInput(query, limit),
            cancellationToken);
    }

    [McpServerTool(Name = "download", ReadOnly = true, OpenWorld = true, UseStructuredContent = true)]
    [Description(DownloadDescription)]
    public static async Task<object> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Page-reference object. Must contain at least 'url'; other metadata such as title and snippet are optional.")] WebDownloadPageRef? page = null,
        [Description("Absolute HTTP or HTTPS URL to download when a full page object is not available.")] string? url = null,
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
        public Task<WebSearchData> SearchAsync(
            [Description("Search query to submit to the search engine.")] string query,
            [Description("Maximum number of results to return. Default 4, max 6.")] int? limit = null,
            CancellationToken cancellationToken = default) =>
            BuiltInWebToolLogic.SearchAsync(
                httpClientFactory,
                logger,
                new WebSearchInput(query, limit),
                cancellationToken);

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
}
