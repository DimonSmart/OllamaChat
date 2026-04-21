using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInWebMcpServerTools
{
    private const string SearchDescription =
        "Search the web with required 'query' and optional 'limit', then return structured candidate page references under 'results'. 'limit' is only a maximum, not a guarantee: the tool may return fewer results, and some results may be partially relevant. For broader coverage, especially when many distinct entities are needed, prefer multiple searches with different phrasings and/or a higher 'limit'. Each result includes a URL, title, provider, and optional search metadata such as thumbnailUrl. Each search.results[] item is directly compatible with the download tool's 'page' input, so downstream plans can pass page objects without projecting to '.url' first. Results are candidate pages, not verified entities. On failure, the tool returns a structured error payload with code, provider, providerAttempts, query, retryability, fallback usage, and technical diagnostics.";
    private const string DownloadDescription =
        "Download a single web page to obtain full source content from a candidate reference or raw URL. Provide exactly one of 'page' or 'url'. Use this when titles, snippets, rankings, or other lightweight records are not enough and you need exact facts, quotes, specs, or detailed extraction from the underlying page. Prefer 'page' when you already have a search result object: each search.results[] item is directly compatible with 'page' and preserves search metadata such as title, provider, snippet, or thumbnailUrl. Use 'url' only when you have a raw absolute URL string. To download multiple search results, bind 'page' from search.results with mode=map so the step runs once per result. On failure, the tool returns a structured error payload with code, host, retryability, and technical diagnostics.";

    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("92b6b4b0-0d4f-4e7f-a6bc-8cd2c4d6622e"),
        key: "built-in-web",
        name: "Built-in Web MCP Server",
        description: "Searches the web and downloads web pages with structured results.",
        registerTools: static builder => builder.WithTools<BuiltInWebMcpServerTools>());

    [McpServerTool(Name = "search", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(WebSearchData))]
    [Description(SearchDescription)]
    public static async Task<object> SearchAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Search query to submit to the search engine.")] string query,
        [Range(1, 10)]
        [Description("Maximum number of results requested, not guaranteed. Search may return fewer results. Default 8, max 10.")]
        int? limit = null,
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

    [McpServerTool(Name = "download", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(WebDownloadData))]
    [Description(DownloadDescription)]
    public static async Task<object> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Page-reference object for acquiring full source content. Prefer passing one search.results[] item directly here; for multiple results, bind page from search.results with mode=map. Must contain at least 'url'; other metadata such as title and snippet are optional.")]
        WebDownloadPageRef? page = null,
        [Description("Raw absolute URL. Use this only when a full page object is not available.")]
        string? url = null,
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
            StructuredContent = JsonSerializer.SerializeToElement(new
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
}
