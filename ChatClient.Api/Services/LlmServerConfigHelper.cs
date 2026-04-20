using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using OpenAI;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text;

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

    public static string GetConfiguredOpenAiApiKey(IConfiguration configuration, LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            return server.ApiKey;

        foreach (var key in GetOpenAiApiKeyConfigKeys(server))
        {
            var configuredValue = configuration[key];
            if (!string.IsNullOrWhiteSpace(configuredValue))
                return configuredValue;
        }

        return string.Empty;
    }

    public static bool UsesOpenAiCompatibleApi(LlmServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.ServerType is ServerType.ChatGpt or ServerType.Azure;
    }

    public static bool IsAzureOpenAiServer(LlmServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.ServerType == ServerType.Azure;
    }

    public static string GetNormalizedOpenAiBaseUrl(LlmServerConfig server, string? defaultBaseUrl = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl)
            ? defaultBaseUrl ?? LlmServerConfig.DefaultOpenAiUrl
            : server.BaseUrl.Trim();

        if (server.ServerType != ServerType.Azure ||
            !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            !IsAzureOpenAiHost(uri.Host))
        {
            return baseUrl.TrimEnd('/');
        }

        var authority = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.TrimEnd('/');

        if (string.IsNullOrEmpty(path) || path == "/")
            return $"{authority}/openai/v1";

        if (string.Equals(path, "/openai", StringComparison.OrdinalIgnoreCase))
            return $"{authority}/openai/v1";

        return $"{authority}{path}";
    }

    public static IReadOnlyList<string> GetConfiguredAzureDeploymentNames(LlmServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return ParseConfiguredNames(server.AzureDeploymentNamesText);
    }

    public static IReadOnlyList<string> ParseConfiguredNames(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return [];

        return rawText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static HttpClient CreateHttpClient(
        LlmServerConfig server,
        string? defaultBaseUrl = null,
        AuthenticationHeaderValue? authorization = null)
    {
        var handler = new HttpClientHandler();
        if (server.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl)
            ? defaultBaseUrl ?? "http://localhost:11434"
            : server.BaseUrl.TrimEnd('/');

        var client = new HttpClient(handler)
        {
            Timeout = GetRequestTimeout(server),
            BaseAddress = new Uri(baseUrl)
        };

        client.DefaultRequestHeaders.Authorization = authorization ?? CreateBasicAuthentication(server.Password);
        return client;
    }

    public static OpenAIClientOptions CreateOpenAIClientOptions(LlmServerConfig server, Uri? endpoint = null)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = GetRequestTimeout(server)
        };

        if (endpoint is not null)
            options.Endpoint = endpoint;

        options.Transport = new HttpClientPipelineTransport(CreateOpenAITransportHttpClient(server, endpoint));
        return options;
    }

    public static HttpClient CreateOpenAITransportHttpClient(LlmServerConfig server, Uri? endpoint = null)
    {
        var handler = new HttpClientHandler();
        if (server.IgnoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var client = new HttpClient(handler)
        {
            Timeout = GetRequestTimeout(server)
        };

        if (endpoint is not null)
            client.BaseAddress = endpoint;

        return client;
    }

    public static TimeSpan GetRequestTimeout(LlmServerConfig server)
    {
        var timeoutSeconds = server.HttpTimeoutSeconds > 0
            ? server.HttpTimeoutSeconds
            : 600;

        return TimeSpan.FromSeconds(timeoutSeconds);
    }

    public static AuthenticationHeaderValue? CreateBasicAuthentication(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return null;

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{password}"));
        return new AuthenticationHeaderValue("Basic", token);
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

    private static IEnumerable<string> GetOpenAiApiKeyConfigKeys(LlmServerConfig server)
    {
        var secretName = server.ApiKeySecretName?.Trim();
        if (!string.IsNullOrWhiteSpace(secretName))
            yield return $"LlmServers:ApiKeys:{secretName}";

        if (server.Id is { } serverId && serverId != Guid.Empty)
            yield return $"LlmServers:ApiKeys:{serverId}";

        yield return "OpenAI:ApiKey";
    }

    private static bool IsAzureOpenAiHost(string host)
    {
        return host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);
    }
}
