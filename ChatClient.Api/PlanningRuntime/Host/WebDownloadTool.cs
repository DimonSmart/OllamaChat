using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Tools;
using HtmlAgilityPack;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class WebDownloadTool(IHttpClientFactory httpClientFactory, ILogger<WebDownloadTool> logger) : ITool
{
    private const int MaxBodyLength = 12000;

    public string Name => "download";

    public ToolPlannerMetadata PlannerMetadata => new(
        "download",
        "Download a single web page by URL and return its title and body text.",
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""}},""required"":[""url""]}")!.AsObject(),
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""},""body"":{""type"":""string""}},""required"":[""url"",""title"",""body""]}")!.AsObject(),
        ["web", "download"],
        ["auto", "each"]);

    public async Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        var url = input.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(url))
            return ResultEnvelope<JsonElement?>.Failure("invalid_input", "Download URL is required.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri) || (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            return ResultEnvelope<JsonElement?>.Failure("invalid_input", "Download URL must be an absolute HTTP or HTTPS URL.");

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OllamaChatPlanning/1.0)");

            using var response = await client.GetAsync(targetUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var document = new HtmlDocument();
            document.LoadHtml(html);

            RemoveNodes(document, "//script|//style|//noscript|//svg");
            var title = document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
            var text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText ?? string.Empty);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > MaxBodyLength)
                text = text[..MaxBodyLength];

            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new
            {
                url = targetUri.ToString(),
                title = string.IsNullOrWhiteSpace(title) ? targetUri.Host : title,
                body = text
            }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Web download timed out for URL {Url}", url);
            return ResultEnvelope<JsonElement?>.Failure("download_timeout", "Timed out while downloading the page.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web download failed for URL {Url}", url);
            return ResultEnvelope<JsonElement?>.Failure("download_failed", ex.Message);
        }
    }

    private static void RemoveNodes(HtmlDocument document, string xpath)
    {
        var nodes = document.DocumentNode.SelectNodes(xpath);
        if (nodes is null)
            return;

        foreach (var node in nodes)
            node.Remove();
    }
}
