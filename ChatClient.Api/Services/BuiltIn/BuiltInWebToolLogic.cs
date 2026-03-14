using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ChatClient.Api.Services.BuiltIn;

internal static class BuiltInWebToolLogic
{
    private const int DefaultLimit = 4;
    private const int MaxLimit = 6;
    private const int MaxContentLength = 12000;

    private static readonly string[] IgnoredHosts =
    [
        "search.brave.com",
        "cdn.search.brave.com",
        "imgs.search.brave.com",
        "tiles.search.brave.com"
    ];

    public static async Task<WebSearchData> SearchAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        WebSearchInput input,
        CancellationToken cancellationToken = default)
    {
        var query = input.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Search query is required.");

        var limit = input.Limit.HasValue
            ? Math.Clamp(input.Limit.Value, 1, MaxLimit)
            : DefaultLimit;

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OllamaChatWebMcp/1.0)");

            var url = $"https://search.brave.com/search?q={UrlEncoder.Default.Encode(query)}";
            var html = await client.GetStringAsync(url, cancellationToken);
            var results = ExtractStructuredResults(html, limit);

            if (results.Count == 0)
                throw new InvalidOperationException("Search returned no structured candidate results.");

            return new WebSearchData(query, results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Web search failed for query {Query}", query);
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    public static async Task<WebDownloadData> DownloadAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        WebDownloadInput input,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveSource(input, out var targetUri, out var page, out var errorMessage))
            throw new InvalidOperationException(errorMessage);

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OllamaChatWebMcp/1.0)");

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

            var title = string.IsNullOrWhiteSpace(page?.Title)
                ? (string.IsNullOrWhiteSpace(downloadedTitle) ? targetUri.Host : downloadedTitle)
                : page.Title;

            return new WebDownloadData(
                Url: targetUri.ToString(),
                Title: title,
                Content: content,
                Snippet: page?.Snippet,
                SiteName: page?.SiteName,
                DisplayUrl: page?.DisplayUrl,
                Age: page?.Age,
                ThumbnailUrl: page?.ThumbnailUrl,
                Position: page?.Position);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Web download timed out for URL {Url}", targetUri);
            throw new InvalidOperationException("Timed out while downloading the page.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Web download failed for URL {Url}", targetUri);
            throw new InvalidOperationException(ex.Message, ex);
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

    private static bool TryResolveSource(
        WebDownloadInput input,
        out Uri targetUri,
        out WebSearchResult? page,
        out string errorMessage)
    {
        targetUri = default!;
        page = null;
        errorMessage = string.Empty;

        var hasPage = input.Page is not null;
        var hasUrl = !string.IsNullOrWhiteSpace(input.Url);

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

        if (hasPage)
        {
            page = input.Page;
            if (string.IsNullOrWhiteSpace(page!.Url))
            {
                errorMessage = "Download input 'page' must contain a non-empty 'url' field.";
                return false;
            }

            if (!TryCreateHttpUri(page.Url, out targetUri))
            {
                errorMessage = "Download URL must be an absolute HTTP or HTTPS URL.";
                return false;
            }

            return true;
        }

        if (!TryCreateHttpUri(input.Url, out targetUri))
        {
            errorMessage = "Download URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

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
