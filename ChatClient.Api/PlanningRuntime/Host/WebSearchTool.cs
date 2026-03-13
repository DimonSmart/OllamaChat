using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Tools;
using HtmlAgilityPack;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class WebSearchTool(IHttpClientFactory httpClientFactory, ILogger<WebSearchTool> logger) : ITool
{
    private const int DefaultLimit = 4;
    private const int MaxLimit = 6;
    private static readonly string[] IgnoredHosts =
    [
        "search.brave.com",
        "cdn.search.brave.com",
        "imgs.search.brave.com",
        "tiles.search.brave.com"
    ];

    public string Name => "search";

    public ToolPlannerMetadata PlannerMetadata => new(
        "search",
        "Search the web and return raw structured search results with URL, title, snippet, and site metadata. The output may be noisy or partially irrelevant and must be checked for relevance before relying on it.",
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""query"":{""type"":""string""},""limit"":{""type"":""number""}},""required"":[""query""]}")!.AsObject(),
        JsonNode.Parse(@"{""type"":""array"",""items"":{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""},""snippet"":{""type"":""string""},""siteName"":{""type"":""string""},""displayUrl"":{""type"":""string""},""age"":{""type"":""string""},""thumbnailUrl"":{""type"":""string""},""position"":{""type"":""integer""}},""required"":[""url"",""title""]}}")!.AsObject(),
        ["web", "search"],
        ["auto"]);

    public async Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        var query = input.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String
            ? queryElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(query))
            return ResultEnvelope<JsonElement?>.Failure("invalid_input", "Search query is required.");

        var limit = input.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, MaxLimit)
            : DefaultLimit;

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OllamaChatPlanning/1.0)");

            var url = $"https://search.brave.com/search?q={UrlEncoder.Default.Encode(query)}";
            var html = await client.GetStringAsync(url, cancellationToken);
            var results = ExtractStructuredResults(html, limit);

            if (results.Count == 0)
                return ResultEnvelope<JsonElement?>.Failure("search_failed", "Search returned no structured candidate results.");

            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(results));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web search failed for query {Query}", query);
            return ResultEnvelope<JsonElement?>.Failure("search_failed", ex.Message);
        }
    }

    private static List<WebSearchResult> ExtractStructuredResults(string html, int limit)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var resultNodes = document.DocumentNode.SelectNodes("//div[@id='results']//div[@data-type='web' and @data-pos]");
        if (resultNodes is null)
            return [];

        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in resultNodes)
        {
            var anchor = node.SelectSingleNode(".//a[contains(@class,'l1')][@href]");
            var link = anchor?.GetAttributeValue("href", string.Empty)?.Trim();
            if (!TryNormalizeSearchResultUrl(link, out var normalizedUrl))
                continue;
            if (!seen.Add(normalizedUrl))
                continue;

            var titleNode = node.SelectSingleNode(".//*[contains(@class,'title')]");
            var title = NormalizeText(titleNode?.GetAttributeValue("title", string.Empty));
            if (string.IsNullOrWhiteSpace(title))
                title = NormalizeText(titleNode?.InnerText);
            if (string.IsNullOrWhiteSpace(title))
                title = normalizedUrl;

            var snippetNode = node.SelectSingleNode(".//div[contains(@class,'generic-snippet')]//div[contains(@class,'content')]");
            var age = NormalizeAge(snippetNode?.SelectSingleNode(".//span[contains(@class,'t-secondary')]")?.InnerText);
            var snippet = NormalizeText(snippetNode?.InnerText);
            if (!string.IsNullOrWhiteSpace(age))
                snippet = StripAgePrefix(snippet, age);

            var siteName = NormalizeText(node.SelectSingleNode(".//div[contains(@class,'site-name-content')]//div[contains(@class,'desktop-small-semibold')]")?.InnerText);
            var displayUrl = NormalizeText(node.SelectSingleNode(".//cite[contains(@class,'snippet-url')]")?.InnerText);
            var thumbnailUrl = NormalizeText(node.SelectSingleNode(".//a[contains(@class,'thumbnail')]//img[@src]")?.GetAttributeValue("src", string.Empty));
            var position = TryParsePosition(node.GetAttributeValue("data-pos", string.Empty));

            results.Add(new WebSearchResult(
                normalizedUrl,
                title,
                NullIfWhiteSpace(snippet),
                NullIfWhiteSpace(siteName),
                NullIfWhiteSpace(displayUrl),
                NullIfWhiteSpace(age),
                NullIfWhiteSpace(thumbnailUrl),
                position));

            if (results.Count >= limit)
                break;
        }

        return results;
    }

    private static bool TryNormalizeSearchResultUrl(string? link, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || IgnoredHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUrl = uri.ToString();
        return true;
    }

    private static int? TryParsePosition(string? value) =>
        int.TryParse(value, out var parsed) ? parsed + 1 : null;

    private static string NormalizeAge(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        return normalized.Trim().TrimEnd('-').Trim();
    }

    private static string StripAgePrefix(string snippet, string age)
    {
        if (string.IsNullOrWhiteSpace(snippet) || string.IsNullOrWhiteSpace(age))
            return snippet;

        var withDash = $"{age} - ";
        if (snippet.StartsWith(withDash, StringComparison.OrdinalIgnoreCase))
            return snippet[withDash.Length..].Trim();

        if (string.Equals(snippet, age, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return snippet;
    }

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(HtmlEntity.DeEntitize(value), @"\s+", " ").Trim();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
