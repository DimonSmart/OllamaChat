using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Tools;

public sealed record WebSearchInput(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int? Limit = null);

public sealed record WebSearchData([property: JsonPropertyName("urls")] List<string> Urls);

public sealed record WebDownloadInput([property: JsonPropertyName("url")] string Url);

public sealed record WebDownloadData(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("text")] string Text);

