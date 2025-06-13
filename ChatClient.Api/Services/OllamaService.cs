#pragma warning disable SKEXP0070
using System.Net.Http.Headers;
using System.Text;

using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using OllamaSharp;

namespace ChatClient.Api.Services;

/// <summary>
/// Lightweight wrapper around <see cref="OllamaApiClient"/> that recreates the underlying client only
/// when the user‑facing connection settings actually change.
/// </summary>
public sealed class OllamaService(
    IConfiguration configuration,
    IUserSettingsService userSettingsService,
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaService> logger) : IDisposable
{
    private OllamaApiClient? _ollamaClient;
    private HttpClient? _httpClient;
    private SettingsSnapshot? _cachedSettings;

    /// <summary>
    /// Returns a cached <see cref="OllamaApiClient"/> instance or rebuilds it when connection settings differ
    /// from the previous snapshot.
    /// </summary>
    private async Task<OllamaApiClient> GetOllamaClientAsync()
    {
        var current = await GetCurrentSettingsAsync();

        // Re‑create the client only if something important changed.
        if (_ollamaClient is null || !_cachedSettings!.Equals(current))
        {
            _ollamaClient?.Dispose();
            _httpClient?.Dispose();

            _httpClient = BuildHttpClient(current);
            _ollamaClient = new OllamaApiClient(_httpClient);

            _cachedSettings = current;
        }

        return _ollamaClient;
    }

    /// <summary>
    /// Public entry point for other services.
    /// </summary>
    public Task<OllamaApiClient> GetClientAsync() => GetOllamaClientAsync();

    #region Models
    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync()
    {
        try
        {
            var client = await GetOllamaClientAsync();
            var models = await client.ListLocalModelsAsync();

            return models.Select(m => new OllamaModel
            {
                Name = m.Name,
                ModifiedAt = m.ModifiedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Size = m.Size,
                Digest = m.Digest,
                SupportsImages = m.Details?.Families?.Contains("clip") == true
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve Ollama models: {Message}", ex.Message);
            return Array.Empty<OllamaModel>();
        }
    }

    #endregion

    #region Helpers
    private async Task<SettingsSnapshot> GetCurrentSettingsAsync()
    {
        var user = await userSettingsService.GetSettingsAsync();

        return new SettingsSnapshot(
            !string.IsNullOrWhiteSpace(user.OllamaServerUrl)
                ? user.OllamaServerUrl
                : configuration["Ollama:BaseUrl"] ?? OllamaDefaults.ServerUrl,
            user.OllamaBasicAuthPassword,
            user.IgnoreSslErrors,
            user.HttpTimeoutSeconds);
    }

    private HttpClient BuildHttpClient(SettingsSnapshot s)
    {
        var handler = new HttpClientHandler();
        if (s.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        var client = httpClientFactory.CreateClient("OllamaClient");
        client.Timeout = TimeSpan.FromSeconds(s.TimeoutSeconds);
        client.DefaultRequestHeaders.Clear();
        client.BaseAddress = new Uri(s.ServerUrl);

        if (!string.IsNullOrWhiteSpace(s.Password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{s.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        // Replace the factory‑created client when we must ignore SSL errors (factory can't set handler).
        if (s.IgnoreSslErrors)
        {
            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(s.TimeoutSeconds),
                BaseAddress = new Uri(s.ServerUrl)
            };

            if (!string.IsNullOrWhiteSpace(s.Password))
            {
                var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{s.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
            }
        }

        return client;
    }

    #endregion

    public void Dispose()
    {
        _ollamaClient?.Dispose();
        _httpClient?.Dispose();
    }

    #region Nested ‑ simple value object for comparing settings
    private sealed record SettingsSnapshot(string ServerUrl, string? Password, bool IgnoreSslErrors, int TimeoutSeconds);
    #endregion
}
