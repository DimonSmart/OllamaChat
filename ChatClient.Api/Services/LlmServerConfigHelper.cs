using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public static class LlmServerConfigHelper
{
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
    /// Temporary solution for compatibility - uses ServiceProvider to resolve dependencies
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
    /// Fallback to Ollama if server not found
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
    /// Creates HttpClient with basic configuration for LLM server
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
