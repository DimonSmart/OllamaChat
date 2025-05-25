using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatClient.Api.Client.Services;

public class ClientUserSettingsService : IUserSettingsService
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private UserSettings _cachedSettings = new();
    private bool _isInitialized = false;

    public ClientUserSettingsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UserSettings> GetSettingsAsync()
    {
        if (_isInitialized)
        {
            return _cachedSettings;
        }

        try
        {
            var settings = await _httpClient.GetFromJsonAsync<UserSettings>("api/settings", _jsonOptions);
            if (settings != null)
            {
                _cachedSettings = settings;
                _isInitialized = true;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading settings: {ex.Message}");
        }

        return _cachedSettings;
    }

    public async Task SaveSettingsAsync(UserSettings settings)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/settings", settings);
            response.EnsureSuccessStatusCode();

            // Update the cached settings
            _cachedSettings = settings;
            _isInitialized = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error saving settings: {ex.Message}");
            throw;
        }
    }
}
