using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Tools;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class WebSearchTool(IHttpClientFactory httpClientFactory, ILogger<WebSearchTool> logger) : ITool
{
    private const int DefaultLimit = 4;
    private const int MaxLimit = 6;
    private static readonly Regex AbsoluteLinkRegex = new("href=\"(https://[^\"#]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        "Search the web and return candidate page URLs.",
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""query"":{""type"":""string""},""limit"":{""type"":""number""}},""required"":[""query""]}")!.AsObject(),
        JsonNode.Parse(@"{""type"":""array"",""items"":{""type"":""string""}}")!.AsObject(),
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

            var queryTerms = query
                .Split([' ', '.', ',', ':', ';', '-', '_', '/', '\\', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term => term.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var links = AbsoluteLinkRegex
                .Matches(html)
                .Select(match => match.Groups[1].Value)
                .Where(link => Uri.TryCreate(link, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    && !IgnoredHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(link => ScoreCandidate(link, queryTerms))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();

            if (links.Length == 0)
                return ResultEnvelope<JsonElement?>.Failure("search_failed", "Search returned no candidate URLs.");

            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(links));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web search failed for query {Query}", query);
            return ResultEnvelope<JsonElement?>.Failure("search_failed", ex.Message);
        }
    }

    private static int ScoreCandidate(string link, IReadOnlyCollection<string> queryTerms)
    {
        var score = 0;
        foreach (var term in queryTerms)
        {
            if (link.Contains(term, StringComparison.OrdinalIgnoreCase))
                score++;
        }

        if (link.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            score += 2;

        return score;
    }
}
