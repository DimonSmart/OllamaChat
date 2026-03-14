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

public sealed record WebDownloadInput(
    [property: JsonPropertyName("page"), Description("Full search result object returned by the search tool.")]
    WebSearchResult? Page = null,
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
