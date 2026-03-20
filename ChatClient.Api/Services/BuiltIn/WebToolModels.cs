using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ChatClient.Api.Services.BuiltIn;

public sealed record WebSearchInput(
    [property: JsonPropertyName("query"), Description("The search query to submit to the web search engine.")]
    string Query,
    [property: JsonPropertyName("limit"), Description("Maximum number of candidate results to return.")]
    int? Limit = null);

public sealed record WebSearchResult(
    [property: JsonPropertyName("url"), Description("Absolute URL of the candidate page.")]
    string Url,
    [property: JsonPropertyName("title"), Description("Title of the candidate page.")]
    string Title,
    [property: JsonPropertyName("snippet"), Description("Snippet or summary returned by the search engine.")]
    string? Snippet = null,
    [property: JsonPropertyName("siteName"), Description("Site or publisher name when available.")]
    string? SiteName = null,
    [property: JsonPropertyName("displayUrl"), Description("Display URL shown by the search engine.")]
    string? DisplayUrl = null,
    [property: JsonPropertyName("age"), Description("Relative or absolute age label shown by the search engine.")]
    string? Age = null,
    [property: JsonPropertyName("thumbnailUrl"), Description("Optional thumbnail image URL returned by the search engine.")]
    string? ThumbnailUrl = null,
    [property: JsonPropertyName("position"), Description("1-based result position.")]
    int? Position = null);

public sealed record WebSearchData(
    [property: JsonPropertyName("query"), Description("The original query used for the search.")]
    string Query,
    [property: JsonPropertyName("results"), Description("Structured candidate page results returned by the search engine.")]
    IReadOnlyList<WebSearchResult> Results);

public sealed record WebDownloadPageRef(
    [property: JsonPropertyName("url"), Description("Absolute URL of the page to download.")]
    string Url,
    [property: JsonPropertyName("title"), Description("Optional title already known for the page.")]
    string? Title = null,
    [property: JsonPropertyName("snippet"), Description("Optional snippet already known for the page.")]
    string? Snippet = null,
    [property: JsonPropertyName("siteName"), Description("Optional site or publisher name already known for the page.")]
    string? SiteName = null,
    [property: JsonPropertyName("displayUrl"), Description("Optional display URL already known for the page.")]
    string? DisplayUrl = null,
    [property: JsonPropertyName("age"), Description("Optional age metadata already known for the page.")]
    string? Age = null,
    [property: JsonPropertyName("thumbnailUrl"), Description("Optional thumbnail URL already known for the page.")]
    string? ThumbnailUrl = null,
    [property: JsonPropertyName("position"), Description("Optional original search result position.")]
    int? Position = null);

public sealed record WebDownloadInput(
    [property: JsonPropertyName("page"), Description("Page reference object. Must contain at least 'url'; other metadata fields are optional.")]
    WebDownloadPageRef? Page = null,
    [property: JsonPropertyName("url"), Description("Absolute HTTP or HTTPS URL to download when a full page object is not available.")]
    string? Url = null);

public sealed record WebDownloadData(
    [property: JsonPropertyName("url"), Description("Absolute URL of the downloaded page.")]
    string Url,
    [property: JsonPropertyName("title"), Description("Title of the downloaded page.")]
    string Title,
    [property: JsonPropertyName("content"), Description("Normalized plain-text content extracted from the page.")]
    string Content,
    [property: JsonPropertyName("snippet"), Description("Original search snippet if one was available.")]
    string? Snippet = null,
    [property: JsonPropertyName("siteName"), Description("Original search site name if one was available.")]
    string? SiteName = null,
    [property: JsonPropertyName("displayUrl"), Description("Original display URL if one was available.")]
    string? DisplayUrl = null,
    [property: JsonPropertyName("age"), Description("Original search age metadata if one was available.")]
    string? Age = null,
    [property: JsonPropertyName("thumbnailUrl"), Description("Original thumbnail URL if one was available.")]
    string? ThumbnailUrl = null,
    [property: JsonPropertyName("position"), Description("Original search result position if one was available.")]
    int? Position = null);

internal sealed record WebToolErrorDetails(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("needsReplan")] bool NeedsReplan,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("details")] IReadOnlyList<string> Details,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("host")] string? Host = null,
    [property: JsonPropertyName("retryable")] bool? Retryable = null,
    [property: JsonPropertyName("httpStatusCode")] int? HttpStatusCode = null,
    [property: JsonPropertyName("technicalMessage")] string? TechnicalMessage = null,
    [property: JsonPropertyName("exceptionType")] string? ExceptionType = null);

internal sealed class WebToolException(
    string code,
    string message,
    WebToolErrorDetails details,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
    public WebToolErrorDetails Details { get; } = details;
}
