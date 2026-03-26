using ChatClient.Api.Services.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace ChatClient.Tests;

public sealed class BuiltInWebSearchLiveTests
{
    private static readonly TimeSpan RealSearchTimeout = TimeSpan.FromMinutes(3);

    [RealWebFact]
    [Trait("Category", "RealWebExploration")]
    public async Task BuiltInWebSearchLogic_WithRealQuery_ReturnsMultipleResults()
    {
        var result = await ExecuteRealSearchAsync("OpenAI");

        Assert.Equal("OpenAI", result.Query);
        Assert.True(result.Results.Count >= 2, $"Expected at least 2 results for a real search query, got {result.Results.Count}.");
    }

    [RealWebFact]
    [Trait("Category", "RealWebExploration")]
    public async Task BuiltInWebSearchLogic_WithRealQueries_RunSequentiallyAndReturnMultipleResults()
    {
        var first = await ExecuteRealSearchAsync("OpenAI");
        var second = await ExecuteRealSearchAsync("Python programming language");

        Assert.True(first.Results.Count >= 2, $"Expected at least 2 results for the first real search query, got {first.Results.Count}.");
        Assert.True(second.Results.Count >= 2, $"Expected at least 2 results for the second real search query, got {second.Results.Count}.");
    }

    private static async Task<WebSearchData> ExecuteRealSearchAsync(string query)
    {
        var cacheDirectory = CreateTempSearchCacheDirectory();
        BuiltInWebToolLogic.ResetSearchStateForTests(cacheDirectory);

        using var cancellation = new CancellationTokenSource(RealSearchTimeout);

        try
        {
            var result = await BuiltInWebToolLogic.SearchAsync(
                new RealHttpClientFactory(),
                NullLogger.Instance,
                new WebSearchInput(query, Limit: 4),
                cancellation.Token);

            Assert.Equal(query, result.Query);
            Assert.True(result.Results.Count > 0, $"Real web search returned no results for '{query}'.");
            Assert.All(
                result.Results,
                static item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Url));
                    Assert.False(string.IsNullOrWhiteSpace(item.Title));
                    Assert.False(string.IsNullOrWhiteSpace(item.Provider));
                });

            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new XunitException($"Real web search for '{query}' did not complete within {RealSearchTimeout}.");
        }
        catch (WebToolException ex)
        {
            throw new XunitException(
                $"Real web search for '{query}' failed with code '{ex.Code}': {ex.Message}. " +
                $"Provider={ex.Details.Provider ?? "<none>"}; Attempts={FormatProviderAttempts(ex.Details.ProviderAttempts)}");
        }
        finally
        {
            BuiltInWebToolLogic.ResetSearchStateForTests();

            try
            {
                if (Directory.Exists(cacheDirectory))
                    Directory.Delete(cacheDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string CreateTempSearchCacheDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "OllamaChat-real-web-search-tests");
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FormatProviderAttempts(IReadOnlyList<object>? providerAttempts)
    {
        if (providerAttempts is null || providerAttempts.Count == 0)
            return "<none>";

        return string.Join(", ", providerAttempts);
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
