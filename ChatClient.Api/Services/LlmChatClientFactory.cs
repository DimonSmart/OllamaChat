using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace ChatClient.Api.Services;

public interface ILlmChatClientFactory
{
    Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default);
}

public sealed class LlmChatClientFactory(
    ILlmServerConfigService llmServerConfigService,
    IUserSettingsService userSettingsService,
    IConfiguration configuration) : ILlmChatClientFactory
{
    public async Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var server = await LlmServerConfigHelper.GetServerConfigAsync(llmServerConfigService, userSettingsService, model.ServerId);
        if (server is null)
            throw new InvalidOperationException($"LLM server '{model.ServerId}' was not found.");

        var credential = new ApiKeyCredential(ResolveApiKey(server));
        var endpoint = server.ServerType == ServerType.Ollama || !string.IsNullOrWhiteSpace(server.BaseUrl)
            ? BuildEndpoint(server)
            : null;
        var client = new OpenAIClient(credential, LlmServerConfigHelper.CreateOpenAIClientOptions(server, endpoint));
        return client.GetChatClient(model.ModelName).AsIChatClient();
    }

    private Uri BuildEndpoint(LlmServerConfig server)
    {
        var baseUrl = server.ServerType is ServerType.ChatGpt or ServerType.Azure
            ? LlmServerConfigHelper.GetNormalizedOpenAiBaseUrl(server, LlmServerConfig.DefaultOpenAiUrl)
            : string.IsNullOrWhiteSpace(server.BaseUrl) ? LlmServerConfig.DefaultOllamaUrl : server.BaseUrl.Trim();
        if (server.ServerType == ServerType.Ollama &&
            !baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            baseUrl = $"{baseUrl.TrimEnd('/')}/v1";
        return new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/");
    }

    private string ResolveApiKey(LlmServerConfig server)
    {
        if (server.ServerType == ServerType.Ollama)
            return "ollama";
        var configuredApiKey = LlmServerConfigHelper.GetConfiguredOpenAiApiKey(configuration, server);
        if (!string.IsNullOrWhiteSpace(configuredApiKey))
            return configuredApiKey;
        throw new InvalidOperationException($"Server '{server.Name}' requires an API key.");
    }
}
