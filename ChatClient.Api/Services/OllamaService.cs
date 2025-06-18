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

    public async Task<IReadOnlyList<OllamaModel>> GetModelsAsync()
    {
        var client = await GetOllamaClientAsync();
        var models = await client.ListLocalModelsAsync();
        return models.Select(m => new OllamaModel
        {
            Name = m.Name,
            ModifiedAt = m.ModifiedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Size = m.Size,
            Digest = m.Digest,
            SupportsImages = m.Details?.Families?.Contains("clip") == true,
            SupportsFunctionCalling = DeterminesFunctionCallingSupport(m)
        }).ToList();
    }

    private async Task<SettingsSnapshot> GetCurrentSettingsAsync()
    {
        var settings = await userSettingsService.GetSettingsAsync();

        return new SettingsSnapshot(
            !string.IsNullOrWhiteSpace(settings.OllamaServerUrl)
                ? settings.OllamaServerUrl
                : configuration["Ollama:BaseUrl"] ?? OllamaDefaults.ServerUrl,
            settings.OllamaBasicAuthPassword,
            settings.IgnoreSslErrors,
            settings.HttpTimeoutSeconds);
    }

    private static HttpClient BuildHttpClient(SettingsSnapshot s)
    {
        var handler = new HttpClientHandler();
        if (s.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(s.TimeoutSeconds),
            BaseAddress = new Uri(s.ServerUrl)
        };

        if (!string.IsNullOrWhiteSpace(s.Password))
        {
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{s.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }
        return client;
    }

    /// <summary>
    /// Experimental! Determines if a model supports function calling based on its name and family.
    /// </summary>
    private static bool DeterminesFunctionCallingSupport(OllamaSharp.Models.Model model)
    {
        // Check if model name contains known function calling capable models
        var modelName = model.Name.ToLowerInvariant();

        // Additional check: if model has tool-related capabilities or specific families
        if (model.Details?.Families != null)
        {
            // Some models might have specific families that indicate tool support
            // This is more speculative but can be refined based on actual model data
            return model.Details.Families.Any(family =>
                family.ToLowerInvariant().Contains("tool") ||
                family.ToLowerInvariant().Contains("function"));
        }

        return false;
    }

    public void Dispose()
    {
        _ollamaClient?.Dispose();
        _httpClient?.Dispose();
    }

    #region Nested ‑ simple value object for comparing settings
    private sealed record SettingsSnapshot(string ServerUrl, string? Password, bool IgnoreSslErrors, int TimeoutSeconds);
    #endregion
}
