using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Tools;

public sealed record WebSearchInput(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int? Limit = null);

public sealed record WebSearchResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("snippet")] string? Snippet = null,
    [property: JsonPropertyName("siteName")] string? SiteName = null,
    [property: JsonPropertyName("displayUrl")] string? DisplayUrl = null,
    [property: JsonPropertyName("age")] string? Age = null,
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl = null,
    [property: JsonPropertyName("position")] int? Position = null);

public sealed record WebDownloadInput(
    [property: JsonPropertyName("page")] WebSearchResult? Page = null,
    [property: JsonPropertyName("url")] string? Url = null);

public sealed record WebDownloadData(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("snippet")] string? Snippet = null,
    [property: JsonPropertyName("siteName")] string? SiteName = null,
    [property: JsonPropertyName("displayUrl")] string? DisplayUrl = null,
    [property: JsonPropertyName("age")] string? Age = null,
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl = null,
    [property: JsonPropertyName("position")] int? Position = null);

