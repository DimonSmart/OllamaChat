using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public static class LlmServerConfigHelper
{
    /// <summary>
    /// Gets server configuration by ID with fallback to default server
    /// </summary>
    /// <param name="llmServerConfigService">LLM server configuration service</param>
    /// <param name="userSettingsService">User settings service</param>
    /// <param name="serverId">Server ID (can be null or Guid.Empty)</param>
    /// <param name="serverType">Server type for filtering (optional)</param>
    /// <returns>Server configuration or null if not found</returns>
    public static async Task<LlmServerConfig?> GetServerConfigAsync(
        ILlmServerConfigService llmServerConfigService,
        IUserSettingsService userSettingsService,
        Guid? serverId = null,
        ServerType? serverType = null)
    {
        var servers = await llmServerConfigService.GetAllAsync();
        var settings = await userSettingsService.GetSettingsAsync();

        return GetServerConfig(servers, settings.DefaultModel.ServerId, serverId, serverType);
    }

    /// <summary>
    /// Получает конфигурацию сервера по ID через ServiceProvider (временное решение для совместимости)
    /// </summary>
    public static async Task<LlmServerConfig?> GetServerConfigAsync(
        IUserSettingsService userSettingsService,
        IServiceProvider serviceProvider,
        Guid? serverId = null,
        ServerType? serverType = null)
    {
        var llmServerConfigService = serviceProvider.GetRequiredService<ILlmServerConfigService>();
        return await GetServerConfigAsync(llmServerConfigService, userSettingsService, serverId, serverType);
    }

    /// <summary>
    /// Получает конфигурацию сервера из списка серверов
    /// </summary>
    public static LlmServerConfig? GetServerConfig(
        IEnumerable<LlmServerConfig> servers,
        Guid? defaultLlmId,
        Guid? serverId = null,
        ServerType? serverType = null)
    {
        var filteredServers = serverType.HasValue
            ? servers.Where(s => s.ServerType == serverType.Value)
            : servers;

        var serverById = TryGetServerById(filteredServers, serverId);
        if (serverById != null)
            return serverById;

        var defaultServer = TryGetDefaultServer(filteredServers, defaultLlmId);
        if (defaultServer != null)
            return defaultServer;

        return filteredServers.FirstOrDefault();
    }

    /// <summary>
    /// Получает тип сервера по ID с fallback на Ollama если сервер не найден
    /// </summary>
    public static async Task<ServerType> GetServerTypeAsync(
        ILlmServerConfigService llmServerConfigService,
        IUserSettingsService userSettingsService,
        Guid? serverId)
    {
        var server = await GetServerConfigAsync(llmServerConfigService, userSettingsService, serverId);
        return server?.ServerType ?? ServerType.Ollama;
    }

    /// <summary>
    /// Создает HttpClient с базовой конфигурацией для LLM сервера
    /// </summary>
    public static HttpClient CreateHttpClient(LlmServerConfig server, string? defaultBaseUrl = null)
    {
        var handler = new HttpClientHandler();
        if (server.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl)
            ? defaultBaseUrl ?? "http://localhost:11434"
            : server.BaseUrl.TrimEnd('/');

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(server.HttpTimeoutSeconds),
            BaseAddress = new Uri(baseUrl)
        };

        return client;
    }

    private static LlmServerConfig? TryGetServerById(IEnumerable<LlmServerConfig> servers, Guid? serverId)
    {
        return serverId.HasValue && serverId.Value != Guid.Empty
            ? servers.FirstOrDefault(s => s.Id == serverId.Value)
            : null;
    }

    private static LlmServerConfig? TryGetDefaultServer(IEnumerable<LlmServerConfig> servers, Guid? defaultLlmId)
    {
        return defaultLlmId.HasValue
            ? servers.FirstOrDefault(s => s.Id == defaultLlmId.Value)
            : null;
    }
}
