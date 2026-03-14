using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInWebMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("92b6b4b0-0d4f-4e7f-a6bc-8cd2c4d6622e"),
        key: "built-in-web",
        name: "Built-in Web MCP Server",
        description: "Searches the web and downloads web pages with structured results.",
        registerTools: static builder => builder.WithTools<BuiltInWebMcpServerTools>());

    [McpServerTool(Name = "search", ReadOnly = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Search the web and return structured candidate page objects under 'results'. The output may still include partially relevant pages and should be checked before making claims.")]
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
    [Description("Download a single web page. Prefer passing a full search-result object via 'page'; the tool returns the same object enriched with 'content'. If only a raw absolute URL is available, pass it via 'url'.")]
    public static Task<WebDownloadData> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        ILogger<BuiltInWebMcpServerTools> logger,
        [Description("Full search-result object returned by the search tool, preserving metadata like title and snippet.")] WebSearchResult? page = null,
        [Description("Absolute HTTP or HTTPS URL to download when a full page object is not available.")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        return BuiltInWebToolLogic.DownloadAsync(
            httpClientFactory,
            logger,
            new WebDownloadInput(page, url),
            cancellationToken);
    }
}
