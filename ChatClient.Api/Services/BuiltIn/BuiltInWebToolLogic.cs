using System.Net;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using ChatClient.Api.Search;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Api.Services.BuiltIn;

internal static class BuiltInWebToolLogic
{
    private const int DefaultLimit = 8;
    private const int MaxLimit = 10;
    private const int MaxContentLength = 12000;
    private const int BraveSearchMaxAttempts = 3;
    private const int SearchFallbackMaxAttempts = 3;
    private const int DownloadMaxAttempts = 3;
    private const string SearchCachePathConfigKey = "BuiltInWeb:SearchCachePath";
    private const string SearchCacheVersion = "v6";
    private const string BraveProviderName = "brave";
    private const string DuckDuckGoProviderName = "duckduckgo";
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

    private enum SearchProviderOutcomeKind
    {
        SuccessWithResults,
        SuccessNoResults,
        ProviderFailed
    }

    private sealed record SearchExtractionResult(
        IReadOnlyList<WebSearchResult> Results,
        bool HasStructuredMarkup);

    private sealed record SearchProviderExecutionResult(
        string Provider,
        SearchProviderOutcomeKind Kind,
        IReadOnlyList<WebSearchResult>? Results,
        WebToolException? Failure,
        int AttemptCount)
    {
        public static SearchProviderExecutionResult SuccessWithResults(
            string provider,
            IReadOnlyList<WebSearchResult> results,
            int attemptCount) =>
            new(provider, SearchProviderOutcomeKind.SuccessWithResults, results, null, attemptCount);

        public static SearchProviderExecutionResult SuccessNoResults(
            string provider,
            int attemptCount) =>
            new(provider, SearchProviderOutcomeKind.SuccessNoResults, null, null, attemptCount);

        public static SearchProviderExecutionResult ProviderFailed(
            string provider,
            WebToolException failure,
            int attemptCount) =>
            new(provider, SearchProviderOutcomeKind.ProviderFailed, null, failure, attemptCount);
    }

    private readonly record struct RetriedHttpResponse(HttpResponseMessage Response, int AttemptCount);

    private interface IWebSearchProvider
    {
        string Name { get; }
        bool RequiresExclusiveGate { get; }
        Task<SearchProviderExecutionResult> SearchAsync(
            HttpClient client,
            ILogger logger,
            string query,
            CancellationToken cancellationToken);
    }

    private sealed class BraveSearchProvider : IWebSearchProvider
    {
        public string Name => BraveProviderName;
        public bool RequiresExclusiveGate => true;

        public Task<SearchProviderExecutionResult> SearchAsync(
            HttpClient client,
            ILogger logger,
            string query,
            CancellationToken cancellationToken) =>
            SearchWithBraveAsync(client, logger, query, cancellationToken);
    }

    private sealed class DuckDuckGoSearchProvider : IWebSearchProvider
    {
        public string Name => DuckDuckGoProviderName;
        public bool RequiresExclusiveGate => false;

        public Task<SearchProviderExecutionResult> SearchAsync(
            HttpClient client,
            ILogger logger,
            string query,
            CancellationToken cancellationToken) =>
            SearchWithDuckDuckGoAsync(client, logger, query, cancellationToken);
    }

    public static async Task<WebSearchData> SearchAsync(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        WebSearchInput input,
        CancellationToken cancellationToken = default)
    {
        var query = input.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            throw CreateSearchFailure(
                code: "search_invalid_input",
                message: "Search query is required.",
                query: input.Query,
                provider: null,
                fallbackTried: false,
                retryable: false);

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
            var outcomes = new List<SearchProviderExecutionResult>();
            foreach (var provider in CreateSearchProviders())
            {
                SearchProviderExecutionResult outcome;
                if (provider.RequiresExclusiveGate)
                {
                    await BraveSearchGate.WaitAsync(cancellationToken);
                    try
                    {
                        if (await TryGetCachedSearchResultAsync(cacheKey, query, limit, cancellationToken) is { } cachedWhileHoldingGate)
                            return cachedWhileHoldingGate;

                        outcome = await provider.SearchAsync(client, logger, query, cancellationToken);
                    }
                    finally
                    {
                        BraveSearchGate.Release();
                    }
                }
                else
                {
                    outcome = await provider.SearchAsync(client, logger, query, cancellationToken);
                }

                outcomes.Add(outcome);
                switch (outcome.Kind)
                {
                    case SearchProviderOutcomeKind.SuccessWithResults:
                        await StoreCachedSearchResultAsync(cacheKey, query, outcome.Results!, cancellationToken);
                        return new WebSearchData(query, outcome.Results!.Take(limit).ToList());
                    case SearchProviderOutcomeKind.SuccessNoResults:
                        logger.LogInformation(
                            "Search provider {Provider} completed after {AttemptCount} attempts but returned no normalized results for query {Query}.",
                            outcome.Provider,
                            outcome.AttemptCount,
                            query);
                        break;
                    case SearchProviderOutcomeKind.ProviderFailed:
                        logger.LogWarning(
                            outcome.Failure,
                            "Search provider {Provider} failed after {AttemptCount} attempts for query {Query}. Trying the next provider if available.",
                            outcome.Provider,
                            outcome.AttemptCount,
                            query);
                        break;
                }
            }

            throw CreateSearchAggregateFailure(query, outcomes);
        }
        catch (WebToolException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Web search failed for query {Query}", query);
            throw CreateSearchFailure(
                code: "search_failed",
                message: string.IsNullOrWhiteSpace(ex.Message)
                    ? "Search failed for an unknown reason."
                    : ex.Message,
                query: query,
                provider: null,
                fallbackTried: false,
                retryable: false,
                exception: ex);
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
            throw CreateDownloadFailure(
                code: "download_invalid_input",
                message: errorMessage,
                targetUri: null,
                retryable: false);

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OllamaChatWebMcp/1.0)");

            var retryResult = await GetWithRetriesAsync(client, logger, targetUri, DownloadMaxAttempts, cancellationToken);
            using var response = retryResult.Response;
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
                Provider: page?.Provider,
                Snippet: page?.Snippet,
                SiteName: page?.SiteName,
                DisplayUrl: page?.DisplayUrl,
                Age: page?.Age,
                ThumbnailUrl: page?.ThumbnailUrl,
                Position: page?.Position);
        }
        catch (WebToolException)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Web download timed out for URL {Url}", targetUri);
            throw CreateDownloadFailure(
                code: "download_timeout",
                message: "Timed out while downloading the page.",
                targetUri: targetUri,
                retryable: true,
                exception: ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Web download failed for URL {Url}", targetUri);
            throw CreateDownloadFailure(
                code: ex.StatusCode is HttpStatusCode.TooManyRequests
                    ? "download_rate_limited"
                    : "download_http_error",
                message: BuildHttpErrorMessage(ex, targetUri),
                targetUri: targetUri,
                retryable: IsRetryableHttpFailure(ex.StatusCode),
                exception: ex,
                statusCode: ex.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Web download failed for URL {Url}", targetUri);
            throw CreateDownloadFailure(
                code: "download_failed",
                message: string.IsNullOrWhiteSpace(ex.Message)
                    ? "Download failed for an unknown reason."
                    : ex.Message,
                targetUri: targetUri,
                retryable: false,
                exception: ex);
        }
    }

    private static WebToolException CreateDownloadFailure(
        string code,
        string message,
        Uri? targetUri,
        bool retryable,
        Exception? exception = null,
        HttpStatusCode? statusCode = null)
    {
        var detailItems = new List<string>();
        if (targetUri is not null)
        {
            detailItems.Add($"url={targetUri}");
            detailItems.Add($"host={targetUri.Host}");
        }

        if (statusCode is not null)
            detailItems.Add($"httpStatusCode={(int)statusCode.Value}");

        detailItems.Add($"retryable={retryable.ToString().ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(exception?.Message))
            detailItems.Add($"technical={NormalizeDiagnosticText(exception.Message)}");

        var details = new WebToolErrorDetails(
            Operation: "download",
            Status: "blocked",
            NeedsReplan: !retryable,
            Type: "error",
            Details: detailItems,
            Url: targetUri?.ToString(),
            Host: targetUri?.Host,
            Retryable: retryable,
            HttpStatusCode: statusCode is null ? null : (int)statusCode.Value,
            TechnicalMessage: NormalizeDiagnosticText(exception?.Message),
            ExceptionType: exception?.GetType().Name);

        return new WebToolException(code, message, details, exception);
    }

    private static WebToolException CreateSearchFailure(
        string code,
        string message,
        string? query,
        string? provider,
        bool fallbackTried,
        bool retryable,
        Exception? exception = null,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<string>? extraDetails = null,
        bool? needsReplan = null,
        string? type = null,
        IReadOnlyList<WebToolProviderAttempt>? providerAttempts = null)
    {
        var detailItems = new List<string>();
        var normalizedQuery = NormalizeDiagnosticText(query);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
            detailItems.Add($"query={normalizedQuery}");

        if (!string.IsNullOrWhiteSpace(provider))
            detailItems.Add($"provider={provider}");

        detailItems.Add($"fallbackTried={fallbackTried.ToString().ToLowerInvariant()}");

        if (statusCode is not null)
            detailItems.Add($"httpStatusCode={(int)statusCode.Value}");

        detailItems.Add($"retryable={retryable.ToString().ToLowerInvariant()}");

        if (extraDetails is not null)
        {
            foreach (var detail in extraDetails)
            {
                var normalizedDetail = NormalizeDiagnosticText(detail);
                if (!string.IsNullOrWhiteSpace(normalizedDetail))
                    detailItems.Add(normalizedDetail);
            }
        }

        if (!string.IsNullOrWhiteSpace(exception?.Message))
            detailItems.Add($"technical={NormalizeDiagnosticText(exception.Message)}");

        var resolvedType = type
            ?? (string.Equals(code, "search_no_results", StringComparison.Ordinal)
                ? "missing"
                : "error");
        var resolvedNeedsReplan = needsReplan
            ?? (string.Equals(code, "search_no_results", StringComparison.Ordinal) || !retryable);

        var details = new WebToolErrorDetails(
            Operation: "search",
            Status: "blocked",
            NeedsReplan: resolvedNeedsReplan,
            Type: resolvedType,
            Details: detailItems,
            Query: normalizedQuery,
            Provider: provider,
            FallbackTried: fallbackTried,
            Retryable: retryable,
            HttpStatusCode: statusCode is null ? null : (int)statusCode.Value,
            TechnicalMessage: NormalizeDiagnosticText(exception?.Message),
            ExceptionType: exception?.GetType().Name,
            ProviderAttempts: providerAttempts);

        return new WebToolException(code, message, details, exception);
    }

    private static WebToolException CreateSearchAggregateFailure(
        string query,
        IReadOnlyList<SearchProviderExecutionResult> outcomes)
    {
        var providerAttempts = outcomes
            .Select((outcome, index) => CreateProviderAttempt(outcome, fallbackUsed: index > 0))
            .ToArray();
        var detailItems = providerAttempts.Select(FormatProviderAttemptDetail).ToArray();
        var fallbackTried = outcomes.Count > 1;
        var lastSuccessfulNoResults = outcomes.LastOrDefault(outcome => outcome.Kind == SearchProviderOutcomeKind.SuccessNoResults);
        if (lastSuccessfulNoResults is not null)
        {
            return CreateSearchFailure(
                code: "search_no_results",
                message: "Search completed successfully but returned no structured candidate results.",
                query: query,
                provider: lastSuccessfulNoResults.Provider,
                fallbackTried: fallbackTried,
                retryable: false,
                extraDetails: detailItems,
                needsReplan: true,
                type: "missing",
                providerAttempts: providerAttempts);
        }

        var lastFailure = outcomes.LastOrDefault(outcome => outcome.Kind == SearchProviderOutcomeKind.ProviderFailed)?.Failure;
        return CreateSearchFailure(
            code: "search_unavailable",
            message: "Search providers were exhausted without returning usable structured results.",
            query: query,
            provider: outcomes.LastOrDefault()?.Provider,
            fallbackTried: fallbackTried,
            retryable: false,
            exception: lastFailure,
            statusCode: lastFailure?.Details.HttpStatusCode is int httpStatusCode
                ? (HttpStatusCode)httpStatusCode
                : null,
            extraDetails: detailItems,
            needsReplan: false,
            type: "error",
            providerAttempts: providerAttempts);
    }

    private static string BuildHttpErrorMessage(HttpRequestException exception, Uri targetUri)
    {
        if (exception.StatusCode is null)
            return $"HTTP request failed while downloading '{targetUri}'.";

        return $"Download failed with HTTP {(int)exception.StatusCode.Value} {exception.StatusCode.Value}.";
    }

    private static string BuildSearchHttpErrorMessage(HttpRequestException exception)
    {
        if (exception.StatusCode is null)
            return "HTTP request failed while searching the web.";

        return $"Search failed with HTTP {(int)exception.StatusCode.Value} {exception.StatusCode.Value}.";
    }

    private static bool IsRetryableHttpFailure(HttpStatusCode? statusCode) =>
        statusCode switch
        {
            null => true,
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ when (int)statusCode.Value >= 500 => true,
            _ => false
        };

    private static string? NormalizeDiagnosticText(string? value)
    {
        var normalized = NormalizeText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static SearchExtractionResult ExtractBraveResults(string html, int limit)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var resultNodes = document.DocumentNode.SelectNodes("//div[@id='results']//div[@data-type='web' and @data-pos]");
        if (resultNodes is null)
            return new SearchExtractionResult([], HasStructuredBraveMarkup(document));

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
            var thumbnailUrl = NormalizeThumbnailUrl(node.SelectSingleNode(".//a[contains(@class,'thumbnail')]//img[@src]")?.GetAttributeValue("src", string.Empty));
            var position = TryParsePosition(node.GetAttributeValue("data-pos", string.Empty));

            results.Add(new WebSearchResult(
                Url: normalizedUrl,
                Title: title,
                Provider: BraveProviderName,
                Snippet: NullIfWhiteSpace(snippet),
                SiteName: NullIfWhiteSpace(siteName),
                DisplayUrl: NullIfWhiteSpace(displayUrl),
                Age: NullIfWhiteSpace(age),
                ThumbnailUrl: thumbnailUrl,
                Position: position));

            if (results.Count >= limit)
                break;
        }

        return new SearchExtractionResult(results, true);
    }

    private static IReadOnlyList<IWebSearchProvider> CreateSearchProviders() =>
    [
        new BraveSearchProvider(),
        new DuckDuckGoSearchProvider()
    ];

    private static WebToolProviderAttempt CreateProviderAttempt(SearchProviderExecutionResult outcome, bool fallbackUsed) =>
        outcome.Kind switch
        {
            SearchProviderOutcomeKind.SuccessWithResults => new WebToolProviderAttempt(
                Provider: outcome.Provider,
                Outcome: "success_with_results",
                AttemptCount: outcome.AttemptCount,
                FallbackUsed: fallbackUsed),
            SearchProviderOutcomeKind.SuccessNoResults => new WebToolProviderAttempt(
                Provider: outcome.Provider,
                Outcome: "success_no_results",
                AttemptCount: outcome.AttemptCount,
                FallbackUsed: fallbackUsed),
            _ => new WebToolProviderAttempt(
                Provider: outcome.Provider,
                Outcome: "provider_failed",
                Code: outcome.Failure?.Code,
                Retryable: outcome.Failure?.Details.Retryable,
                HttpStatusCode: outcome.Failure?.Details.HttpStatusCode,
                TechnicalMessage: outcome.Failure?.Details.TechnicalMessage,
                AttemptCount: outcome.AttemptCount,
                FallbackUsed: fallbackUsed)
        };

    private static string FormatProviderAttemptDetail(WebToolProviderAttempt attempt)
    {
        var detail = $"provider={attempt.Provider}; outcome={attempt.Outcome}";
        if (!string.IsNullOrWhiteSpace(attempt.Code))
            detail += $"; code={attempt.Code}";
        if (attempt.Retryable is not null)
            detail += $"; retryable={attempt.Retryable.Value.ToString().ToLowerInvariant()}";
        if (attempt.HttpStatusCode is not null)
            detail += $"; httpStatusCode={attempt.HttpStatusCode.Value}";
        if (attempt.AttemptCount is not null)
            detail += $"; attempts={attempt.AttemptCount.Value}";
        if (attempt.FallbackUsed is not null)
            detail += $"; fallbackUsed={attempt.FallbackUsed.Value.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(attempt.TechnicalMessage))
            detail += $"; technical={attempt.TechnicalMessage}";

        return detail;
    }

    private static bool HasStructuredBraveMarkup(HtmlDocument document) =>
        document.DocumentNode.SelectSingleNode("//div[@id='results']") is not null;

    private static bool HasStructuredDuckDuckGoMarkup(HtmlDocument document) =>
        document.DocumentNode.SelectSingleNode("//div[contains(@class,'results')]") is not null
        || document.DocumentNode.SelectSingleNode("//div[contains(@class,'result')]") is not null;

    private static bool TryResolveSource(
        WebDownloadInput input,
        out Uri targetUri,
        out WebDownloadPageRef? page,
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

    private static string? NormalizeThumbnailUrl(string? value)
    {
        var normalized = NullIfWhiteSpace(NormalizeText(value));
        if (normalized is null)
            return null;

        return SearchResultUrlNormalizer.NormalizeImageUrl(normalized);
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

    private static async Task<SearchProviderExecutionResult> SearchWithBraveAsync(
        HttpClient client,
        ILogger logger,
        string query,
        CancellationToken cancellationToken)
    {
        await WaitForBraveSearchCooldownAsync(logger, cancellationToken);
        RegisterBraveSearchCooldown(BraveSearchMinInterval);

        var url = $"https://search.brave.com/search?q={UrlEncoder.Default.Encode(query)}";
        try
        {
            var retryResult = await GetWithRetriesAsync(client, logger, url, BraveSearchMaxAttempts, cancellationToken, BraveProviderName);
            using var response = retryResult.Response;
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var extraction = ExtractBraveResults(html, MaxLimit);

            if (extraction.Results.Count > 0)
                return SearchProviderExecutionResult.SuccessWithResults(BraveProviderName, extraction.Results, retryResult.AttemptCount);

            if (extraction.HasStructuredMarkup)
            {
                return SearchProviderExecutionResult.SuccessNoResults(
                    BraveProviderName,
                    retryResult.AttemptCount);
            }

            return SearchProviderExecutionResult.ProviderFailed(
                BraveProviderName,
                CreateSearchFailure(
                    code: "search_provider_invalid_markup",
                    message: "Brave search returned markup that could not be normalized into structured results.",
                    query: query,
                    provider: BraveProviderName,
                    fallbackTried: false,
                    retryable: false,
                    extraDetails:
                    [
                        $"providerFailed={BraveProviderName}"
                    ]),
                retryResult.AttemptCount);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Brave web search timed out for query {Query}", query);
            return SearchProviderExecutionResult.ProviderFailed(
                BraveProviderName,
                CreateSearchFailure(
                    code: "search_timeout",
                    message: "Timed out while searching the web.",
                    query: query,
                    provider: BraveProviderName,
                    fallbackTried: false,
                    retryable: true,
                    exception: ex),
                BraveSearchMaxAttempts);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Brave web search failed for query {Query}", query);
            return SearchProviderExecutionResult.ProviderFailed(
                BraveProviderName,
                CreateSearchFailure(
                    code: ex.StatusCode is HttpStatusCode.TooManyRequests
                        ? "search_rate_limited"
                        : "search_http_error",
                    message: BuildSearchHttpErrorMessage(ex),
                    query: query,
                    provider: BraveProviderName,
                    fallbackTried: false,
                    retryable: IsRetryableHttpFailure(ex.StatusCode),
                    exception: ex,
                    statusCode: ex.StatusCode),
                BraveSearchMaxAttempts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Brave web search failed for query {Query}", query);
            return SearchProviderExecutionResult.ProviderFailed(
                BraveProviderName,
                CreateSearchFailure(
                    code: "search_failed",
                    message: string.IsNullOrWhiteSpace(ex.Message)
                        ? "Search failed for an unknown reason."
                        : ex.Message,
                    query: query,
                    provider: BraveProviderName,
                    fallbackTried: false,
                    retryable: false,
                    exception: ex),
                BraveSearchMaxAttempts);
        }
    }

    private static async Task<SearchProviderExecutionResult> SearchWithDuckDuckGoAsync(
        HttpClient client,
        ILogger logger,
        string query,
        CancellationToken cancellationToken)
    {
        var url = $"https://html.duckduckgo.com/html/?q={UrlEncoder.Default.Encode(query)}";
        try
        {
            var retryResult = await GetWithRetriesAsync(client, logger, url, SearchFallbackMaxAttempts, cancellationToken, DuckDuckGoProviderName);
            using var response = retryResult.Response;
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var extraction = ExtractDuckDuckGoResults(html, MaxLimit);

            if (extraction.Results.Count > 0)
                return SearchProviderExecutionResult.SuccessWithResults(DuckDuckGoProviderName, extraction.Results, retryResult.AttemptCount);

            if (extraction.HasStructuredMarkup)
            {
                return SearchProviderExecutionResult.SuccessNoResults(
                    DuckDuckGoProviderName,
                    retryResult.AttemptCount);
            }

            return SearchProviderExecutionResult.ProviderFailed(
                DuckDuckGoProviderName,
                CreateSearchFailure(
                    code: "search_provider_invalid_markup",
                    message: "DuckDuckGo HTML search returned markup that could not be normalized into structured results.",
                    query: query,
                    provider: DuckDuckGoProviderName,
                    fallbackTried: true,
                    retryable: false,
                    extraDetails:
                    [
                        $"providerFailed={DuckDuckGoProviderName}"
                    ]),
                retryResult.AttemptCount);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "DuckDuckGo web search timed out for query {Query}", query);
            return SearchProviderExecutionResult.ProviderFailed(
                DuckDuckGoProviderName,
                CreateSearchFailure(
                    code: "search_timeout",
                    message: "Timed out while searching the web.",
                    query: query,
                    provider: DuckDuckGoProviderName,
                    fallbackTried: true,
                    retryable: true,
                    exception: ex),
                SearchFallbackMaxAttempts);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "DuckDuckGo web search failed for query {Query}", query);
            return SearchProviderExecutionResult.ProviderFailed(
                DuckDuckGoProviderName,
                CreateSearchFailure(
                    code: ex.StatusCode is HttpStatusCode.TooManyRequests
                        ? "search_rate_limited"
                        : "search_http_error",
                    message: BuildSearchHttpErrorMessage(ex),
                    query: query,
                    provider: DuckDuckGoProviderName,
                    fallbackTried: true,
                    retryable: IsRetryableHttpFailure(ex.StatusCode),
                    exception: ex,
                    statusCode: ex.StatusCode),
                SearchFallbackMaxAttempts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DuckDuckGo web search failed for query {Query}", query);
            return SearchProviderExecutionResult.ProviderFailed(
                DuckDuckGoProviderName,
                CreateSearchFailure(
                    code: "search_failed",
                    message: string.IsNullOrWhiteSpace(ex.Message)
                        ? "Search failed for an unknown reason."
                        : ex.Message,
                    query: query,
                    provider: DuckDuckGoProviderName,
                    fallbackTried: true,
                    retryable: false,
                    exception: ex),
                SearchFallbackMaxAttempts);
        }
    }

    private static SearchExtractionResult ExtractDuckDuckGoResults(string html, int limit)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var resultNodes = document.DocumentNode.SelectNodes("//div[contains(@class,'result') and contains(@class,'results_links')]");
        if (resultNodes is null)
            return new SearchExtractionResult([], HasStructuredDuckDuckGoMarkup(document));

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
                Provider: DuckDuckGoProviderName,
                Snippet: NullIfWhiteSpace(snippet),
                SiteName: NullIfWhiteSpace(siteName),
                DisplayUrl: NullIfWhiteSpace(displayUrl),
                Age: null,
                ThumbnailUrl: null,
                Position: results.Count + 1));

            if (results.Count >= limit)
                break;
        }

        return new SearchExtractionResult(results, true);
    }

    private static async Task<RetriedHttpResponse> GetWithRetriesAsync(
        HttpClient client,
        ILogger logger,
        string url,
        int maxAttempts,
        CancellationToken cancellationToken,
        string? requestLabel = null) =>
        await GetWithRetriesAsync(client, logger, new Uri(url), maxAttempts, cancellationToken, requestLabel);

    private static async Task<RetriedHttpResponse> GetWithRetriesAsync(
        HttpClient client,
        ILogger logger,
        Uri url,
        int maxAttempts,
        CancellationToken cancellationToken,
        string? requestLabel = null)
    {
        Exception? lastError = null;
        var requestTarget = string.IsNullOrWhiteSpace(requestLabel) ? url.ToString() : requestLabel;

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
                        "Web request to {RequestTarget} hit rate limit on attempt {Attempt}/{MaxAttempts}. Retrying after {DelayMs} ms.",
                        requestTarget,
                        attempt,
                        maxAttempts,
                        (int)delay.TotalMilliseconds);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new HttpRequestException(
                        $"Web request to '{url}' was rate-limited (HTTP 429) after {maxAttempts} attempts.",
                        inner: null,
                        statusCode: HttpStatusCode.TooManyRequests);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Web request to '{url}' failed with HTTP {(int)response.StatusCode} {response.StatusCode}.",
                        inner: null,
                        statusCode: response.StatusCode);
                }

                return new RetriedHttpResponse(response, attempt);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                response?.Dispose();
                if (attempt == maxAttempts)
                    break;

                var delay = GetRetryDelay(response, attempt);
                logger.LogInformation(
                    ex,
                    "Web request to {RequestTarget} timed out on attempt {Attempt}/{MaxAttempts}. Retrying after {DelayMs} ms.",
                    requestTarget,
                    attempt,
                    maxAttempts,
                    (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                response?.Dispose();
                if (attempt == maxAttempts)
                    break;

                var delay = GetRetryDelay(response, attempt);
                logger.LogInformation(
                    ex,
                    "Web request to {RequestTarget} failed on attempt {Attempt}/{MaxAttempts}. Retrying after {DelayMs} ms.",
                    requestTarget,
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

        logger.LogWarning(
            lastError,
            "Web request to {RequestTarget} exhausted {MaxAttempts} attempts without a successful response.",
            requestTarget,
            maxAttempts);

        if (lastError is not null)
            ExceptionDispatchInfo.Capture(lastError).Throw();

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
