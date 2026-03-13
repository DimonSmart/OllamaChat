using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Tools;
using HtmlAgilityPack;

namespace ChatClient.Api.PlanningRuntime.Host;

public sealed class WebDownloadTool(IHttpClientFactory httpClientFactory, ILogger<WebDownloadTool> logger) : ITool
{
    private const int MaxContentLength = 12000;

    public string Name => "download";

    public ToolPlannerMetadata PlannerMetadata => new(
        "download",
        "Download a single web page. Prefer passing a full search-result object via 'page'; the tool returns the same object enriched with 'content'. If only a raw absolute URL is available, pass it via 'url' and the tool returns a minimal object with url, title, and content.",
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""page"":{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""},""snippet"":{""type"":""string""},""siteName"":{""type"":""string""},""displayUrl"":{""type"":""string""},""age"":{""type"":""string""},""thumbnailUrl"":{""type"":""string""},""position"":{""type"":""integer""}}},""url"":{""type"":""string""}}}")!.AsObject(),
        JsonNode.Parse(@"{""type"":""object"",""properties"":{""url"":{""type"":""string""},""title"":{""type"":""string""},""snippet"":{""type"":""string""},""siteName"":{""type"":""string""},""displayUrl"":{""type"":""string""},""age"":{""type"":""string""},""thumbnailUrl"":{""type"":""string""},""position"":{""type"":""integer""},""content"":{""type"":""string""}},""required"":[""url"",""title"",""content""]}")!.AsObject(),
        ["web", "download"],
        ["auto", "each"]);

    public async Task<ResultEnvelope<JsonElement?>> ExecuteAsync(JsonElement input, CancellationToken cancellationToken = default)
    {
        if (!TryResolveSource(input, out var targetUri, out var resultObject, out var errorMessage))
            return ResultEnvelope<JsonElement?>.Failure("invalid_input", errorMessage);

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
            var downloadedTitle = NormalizeText(document.DocumentNode.SelectSingleNode("//title")?.InnerText);
            var content = HtmlEntity.DeEntitize(document.DocumentNode.InnerText ?? string.Empty);
            content = Regex.Replace(content, @"\s+", " ").Trim();
            if (content.Length > MaxContentLength)
                content = content[..MaxContentLength];

            resultObject["url"] = targetUri.ToString();
            if (!TryGetString(resultObject, "title", out _))
                resultObject["title"] = string.IsNullOrWhiteSpace(downloadedTitle) ? targetUri.Host : downloadedTitle;
            resultObject["content"] = content;

            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(resultObject));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            logger.LogWarning("Web download timed out for URL {Url}", targetUri);
            return ResultEnvelope<JsonElement?>.Failure("download_timeout", "Timed out while downloading the page.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web download failed for URL {Url}", targetUri);
            return ResultEnvelope<JsonElement?>.Failure("download_failed", ex.Message);
        }
    }

    private static bool TryResolveSource(
        JsonElement input,
        out Uri targetUri,
        out JsonObject resultObject,
        out string errorMessage)
    {
        targetUri = default!;
        resultObject = default!;
        errorMessage = string.Empty;

        var hasPage = input.TryGetProperty("page", out var pageElement) && pageElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        var hasUrl = input.TryGetProperty("url", out var urlElement) && urlElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

        if (hasPage && hasUrl)
        {
            errorMessage = "Download input must provide either 'page' or 'url', not both.";
            return false;
        }

        if (!hasPage && !hasUrl)
        {
            errorMessage = "Download input must provide either 'page' or 'url'.";
            return false;
        }

        return hasPage
            ? TryResolvePageSource(pageElement, out targetUri, out resultObject, out errorMessage)
            : TryResolveUrlSource(urlElement, out targetUri, out resultObject, out errorMessage);
    }

    private static bool TryResolvePageSource(
        JsonElement pageElement,
        out Uri targetUri,
        out JsonObject resultObject,
        out string errorMessage)
    {
        targetUri = default!;
        resultObject = default!;
        errorMessage = string.Empty;

        if (pageElement.ValueKind == JsonValueKind.String)
            return TryResolveUrlSource(pageElement, out targetUri, out resultObject, out errorMessage);

        if (pageElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "Download input 'page' must be an object with a 'url' field or a raw URL string.";
            return false;
        }

        resultObject = JsonNode.Parse(pageElement.GetRawText())?.AsObject()
            ?? new JsonObject();

        if (!TryGetString(resultObject, "url", out var url))
        {
            errorMessage = "Download input 'page' must contain a non-empty 'url' field.";
            return false;
        }

        if (!TryCreateHttpUri(url, out targetUri))
        {
            errorMessage = "Download URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        resultObject["url"] = targetUri.ToString();
        return true;
    }

    private static bool TryResolveUrlSource(
        JsonElement urlElement,
        out Uri targetUri,
        out JsonObject resultObject,
        out string errorMessage)
    {
        targetUri = default!;
        resultObject = default!;
        errorMessage = string.Empty;

        if (urlElement.ValueKind != JsonValueKind.String)
        {
            errorMessage = "Download input 'url' must be a string.";
            return false;
        }

        var url = urlElement.GetString()?.Trim();
        if (!TryCreateHttpUri(url, out targetUri))
        {
            errorMessage = "Download URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        resultObject = new JsonObject
        {
            ["url"] = targetUri.ToString()
        };

        return true;
    }

    private static void RemoveNodes(HtmlDocument document, string xpath)
    {
        var nodes = document.DocumentNode.SelectNodes(xpath);
        if (nodes is null)
            return;

        foreach (var node in nodes)
            node.Remove();
    }

    private static bool TryCreateHttpUri(string? value, out Uri targetUri)
    {
        targetUri = default!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsedUri))
            return false;

        if (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps)
            return false;

        targetUri = parsedUri;
        return true;
    }

    private static bool TryGetString(JsonObject source, string propertyName, out string? value)
    {
        value = null;
        if (source[propertyName] is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var parsed))
            return false;

        parsed = parsed?.Trim();
        if (string.IsNullOrWhiteSpace(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(HtmlEntity.DeEntitize(value), @"\s+", " ").Trim();
}
