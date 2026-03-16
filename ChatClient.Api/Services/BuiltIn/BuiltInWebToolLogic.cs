using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Api.Services.BuiltIn;

internal static class BuiltInWebToolLogic
{
    private const int DefaultLimit = 4;
    private const int MaxLimit = 6;
    private const int MaxContentLength = 12000;
    private const int SearchMaxAttempts = 5;
    private const int DownloadMaxAttempts = 3;
    private const string SearchCachePathConfigKey = "BuiltInWeb:SearchCachePath";
    private const string SearchCacheVersion = "v2";
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan BraveSearchMinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitMaxRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim BraveSearchGate = new(1, 1);
    private static readonly object BraveSearchStateLock = new();
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };
    private static readonly Lazy<string> SearchCacheDirectory = new(ResolveSearchCacheDirectory);
    private static DateTimeOffset _nextBraveSearchAllowedAt = DateTimeOffset.MinValue;
    private static string? _searchCacheDirectoryOverrideForTests;

    private static readonly string[] IgnoredHosts =
    [
        "search.brave.com",
        "cdn.search.brave.com",
        "imgs.search.brave.com",
        "tiles.search.brave.com"
    ];

    private sealed record SearchCacheEntry(
        string Version,
        string Query,
        DateTimeOffset CachedAtUtc,
        List<WebSearchResult> Results);

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
        var cacheKey = BuildSearchCacheKey(query);

        if (await TryGetCachedSearchResultAsync(cacheKey, query, limit, cancellationToken) is { } cached)
            return cached;

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OllamaChatWebMcp/1.0)");
            List<WebSearchResult>? results = null;
            Exception? braveFailure = null;

            await BraveSearchGate.WaitAsync(cancellationToken);
            try
            {
                if (await TryGetCachedSearchResultAsync(cacheKey, query, limit, cancellationToken) is { } cachedWhileHoldingGate)
                    return cachedWhileHoldingGate;

                try
                {
                    results = await SearchWithBraveAsync(client, logger, query, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    braveFailure = ex;
                    logger.LogWarning(ex, "Brave search failed for query {Query}. Falling back to DuckDuckGo HTML results.", query);
                }
            }
            finally
            {
                BraveSearchGate.Release();
            }

            results ??= await SearchWithDuckDuckGoAsync(client, logger, query, cancellationToken);
            if (results.Count == 0)
                throw braveFailure ?? new InvalidOperationException("Search returned no structured candidate results.");

            await StoreCachedSearchResultAsync(cacheKey, query, results, cancellationToken);
            return new WebSearchData(query, results.Take(limit).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Web search failed for query {Query}", query);
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    internal static void ResetSearchStateForTests(string? searchCacheDirectoryOverride = null)
    {
        _searchCacheDirectoryOverrideForTests = searchCacheDirectoryOverride;
        lock (BraveSearchStateLock)
            _nextBraveSearchAllowedAt = DateTimeOffset.MinValue;
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

            using var response = await GetWithRetriesAsync(client, logger, targetUri, DownloadMaxAttempts, cancellationToken);
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

    private static string BuildSearchCacheKey(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{SearchCacheVersion}\n{normalized}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<WebSearchData?> TryGetCachedSearchResultAsync(
        string cacheKey,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var cachePath = GetSearchCachePath(cacheKey);
        if (!File.Exists(cachePath))
            return null;

        try
        {
            await using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0)
            {
                TryDeleteCacheFileQuietly(cachePath);
                return null;
            }

            var cached = await JsonSerializer.DeserializeAsync<SearchCacheEntry>(stream, CacheJsonOptions, cancellationToken);
            if (cached is null
                || !string.Equals(cached.Version, SearchCacheVersion, StringComparison.Ordinal)
                || !string.Equals(cached.Query.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase)
                || cached.CachedAtUtc + SearchCacheTtl <= DateTimeOffset.UtcNow
                || cached.Results.Count == 0)
            {
                TryDeleteCacheFileQuietly(cachePath);
                return null;
            }

            return new WebSearchData(query, cached.Results.Take(limit).ToList());
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            TryDeleteCacheFileQuietly(cachePath);
            return null;
        }
    }

    private static async Task StoreCachedSearchResultAsync(
        string cacheKey,
        string query,
        IReadOnlyList<WebSearchResult> results,
        CancellationToken cancellationToken)
    {
        var directory = GetSearchCacheDirectory();
        Directory.CreateDirectory(directory);

        var cachePath = GetSearchCachePath(cacheKey);
        var cacheEntry = new SearchCacheEntry(
            SearchCacheVersion,
            query,
            DateTimeOffset.UtcNow,
            results.ToList());

        await File.WriteAllTextAsync(
            cachePath,
            JsonSerializer.Serialize(cacheEntry, CacheJsonOptions),
            cancellationToken);
    }

    private static string GetSearchCachePath(string cacheKey) =>
        Path.Combine(GetSearchCacheDirectory(), $"{cacheKey}.json");

    private static string GetSearchCacheDirectory() =>
        string.IsNullOrWhiteSpace(_searchCacheDirectoryOverrideForTests)
            ? SearchCacheDirectory.Value
            : Path.GetFullPath(_searchCacheDirectoryOverrideForTests);

    private static string ResolveSearchCacheDirectory()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        return StoragePathResolver.ResolveUserPath(
            configuration,
            configuration[SearchCachePathConfigKey],
            FilePathConstants.DefaultWebSearchCacheDirectory);
    }

    private static void TryDeleteCacheFileQuietly(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch
        {
            // Ignore broken cache cleanup failures and refetch on the next attempt.
        }
    }

    private static async Task WaitForBraveSearchCooldownAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var delay = GetBraveSearchCooldownRemaining();
        if (delay <= TimeSpan.Zero)
            return;

        logger.LogInformation(
            "Waiting {DelayMs} ms before the next Brave search request to avoid rate limiting.",
            (int)delay.TotalMilliseconds);
        await Task.Delay(delay, cancellationToken);
    }

    private static TimeSpan GetBraveSearchCooldownRemaining()
    {
        lock (BraveSearchStateLock)
        {
            var now = DateTimeOffset.UtcNow;
            return _nextBraveSearchAllowedAt > now
                ? _nextBraveSearchAllowedAt - now
                : TimeSpan.Zero;
        }
    }

    private static void RegisterBraveSearchCooldown(TimeSpan delay)
    {
        var candidate = DateTimeOffset.UtcNow + delay;

        lock (BraveSearchStateLock)
        {
            if (candidate > _nextBraveSearchAllowedAt)
                _nextBraveSearchAllowedAt = candidate;
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task<List<WebSearchResult>> SearchWithBraveAsync(
        HttpClient client,
        ILogger logger,
        string query,
        CancellationToken cancellationToken)
    {
        await WaitForBraveSearchCooldownAsync(logger, cancellationToken);
        RegisterBraveSearchCooldown(BraveSearchMinInterval);

        var url = $"https://search.brave.com/search?q={UrlEncoder.Default.Encode(query)}";
        using var response = await GetWithRetriesAsync(client, logger, url, SearchMaxAttempts, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var results = ExtractStructuredResults(html, MaxLimit);

        if (results.Count == 0)
            throw new InvalidOperationException("Brave search returned no structured candidate results.");

        return results;
    }

    private static async Task<List<WebSearchResult>> SearchWithDuckDuckGoAsync(
        HttpClient client,
        ILogger logger,
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"https://html.duckduckgo.com/html/?q={UrlEncoder.Default.Encode(query)}";
        using var response = await GetWithRetriesAsync(client, logger, url, SearchMaxAttempts, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var results = ExtractDuckDuckGoResults(html, MaxLimit);

        if (results.Count == 0)
            throw new InvalidOperationException("DuckDuckGo HTML search returned no structured candidate results.");

        return results;
    }

    private static List<WebSearchResult> ExtractDuckDuckGoResults(string html, int limit)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var resultNodes = document.DocumentNode.SelectNodes("//div[contains(@class,'result') and contains(@class,'results_links')]");
        if (resultNodes is null)
            return [];

        var results = new List<WebSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in resultNodes)
        {
            var anchor = node.SelectSingleNode(".//a[contains(@class,'result__a')][@href]");
            var link = anchor?.GetAttributeValue("href", string.Empty)?.Trim();
            if (!TryNormalizeDuckDuckGoResultUrl(link, out var normalizedUrl))
                continue;
            if (!seen.Add(normalizedUrl))
                continue;

            var title = NormalizeText(anchor?.InnerText);
            if (string.IsNullOrWhiteSpace(title))
                title = normalizedUrl;

            var snippet = NormalizeText(node.SelectSingleNode(".//a[contains(@class,'result__snippet')]")?.InnerText);
            var displayUrl = NormalizeText(node.SelectSingleNode(".//a[contains(@class,'result__url')]")?.InnerText);
            var siteName = TryGetHostLabel(normalizedUrl);

            results.Add(new WebSearchResult(
                Url: normalizedUrl,
                Title: title,
                Snippet: NullIfWhiteSpace(snippet),
                SiteName: NullIfWhiteSpace(siteName),
                DisplayUrl: NullIfWhiteSpace(displayUrl),
                Age: null,
                ThumbnailUrl: null,
                Position: results.Count + 1));

            if (results.Count >= limit)
                break;
        }

        return results;
    }

    private static async Task<HttpResponseMessage> GetWithRetriesAsync(
        HttpClient client,
        ILogger logger,
        string url,
        int maxAttempts,
        CancellationToken cancellationToken) =>
        await GetWithRetriesAsync(client, logger, new Uri(url), maxAttempts, cancellationToken);

    private static async Task<HttpResponseMessage> GetWithRetriesAsync(
        HttpClient client,
        ILogger logger,
        Uri url,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await client.GetAsync(url, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < maxAttempts)
                {
                    var delay = GetRetryDelay(response, attempt);
                    if (IsBraveSearchUri(url))
                        RegisterBraveSearchCooldown(delay);
                    logger.LogInformation(
                        "Web request to {Url} hit rate limit on attempt {Attempt}/{MaxAttempts}. Retrying after {DelayMs} ms.",
                        url,
                        attempt,
                        maxAttempts,
                        (int)delay.TotalMilliseconds);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new InvalidOperationException(
                        $"Web request to '{url}' was rate-limited (HTTP 429) after {maxAttempts} attempts.");
                }

                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                lastError = ex;
                response?.Dispose();
                var delay = GetRetryDelay(response, attempt);
                logger.LogInformation(
                    ex,
                    "Web request to {Url} timed out on attempt {Attempt}/{MaxAttempts}. Retrying after {DelayMs} ms.",
                    url,
                    attempt,
                    maxAttempts,
                    (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                response?.Dispose();
                var delay = GetRetryDelay(response, attempt);
                logger.LogInformation(
                    ex,
                    "Web request to {Url} failed on attempt {Attempt}/{MaxAttempts}. Retrying after {DelayMs} ms.",
                    url,
                    attempt,
                    maxAttempts,
                    (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                response?.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Web request failed for '{url}' after {maxAttempts} attempts.",
            lastError);
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        var maxDelay = response?.StatusCode == HttpStatusCode.TooManyRequests
            ? RateLimitMaxRetryDelay
            : DefaultMaxRetryDelay;
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return CapDelay(delta, maxDelay);

        if (retryAfter?.Date is { } retryAt)
        {
            var computed = retryAt - DateTimeOffset.UtcNow;
            if (computed > TimeSpan.Zero)
                return CapDelay(computed, maxDelay);
        }

        if (response?.StatusCode == HttpStatusCode.TooManyRequests
            && response.RequestMessage?.RequestUri is { } requestUri
            && IsBraveSearchUri(requestUri))
            return CapDelay(TimeSpan.FromSeconds(Math.Min(15 * attempt, 60)), maxDelay);

        return CapDelay(TimeSpan.FromMilliseconds(750 * attempt), maxDelay);
    }

    private static bool IsBraveSearchUri(Uri url) =>
        string.Equals(url.Host, "search.brave.com", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan CapDelay(TimeSpan delay, TimeSpan maxDelay) =>
        delay > maxDelay ? maxDelay : delay;

    private static bool TryNormalizeDuckDuckGoResultUrl(string? link, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(link))
            return false;

        var candidate = link.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{link}"
            : link;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;

        if (string.Equals(uri.Host, "duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            && TryGetQueryParameter(uri, "uddg", out var targetUrl))
        {
            candidate = Uri.UnescapeDataString(targetUrl);
        }

        return TryNormalizeSearchResultUrl(candidate, out normalizedUrl);
    }

    private static bool TryGetQueryParameter(Uri uri, string key, out string value)
    {
        value = string.Empty;
        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 0 || !string.Equals(pair[0], key, StringComparison.OrdinalIgnoreCase))
                continue;

            value = pair.Length == 2 ? pair[1] : string.Empty;
            return true;
        }

        return false;
    }

    private static string? TryGetHostLabel(string absoluteUrl)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host["www.".Length..]
            : host;
    }
}
