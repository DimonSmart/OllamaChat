using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace ChatClient.Api.PlanningRuntime.Host;

public interface IPlanningChatClientFactory
{
    Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default);
}

public sealed class PlanningChatClientFactory(
    ILlmServerConfigService llmServerConfigService,
    IUserSettingsService userSettingsService,
    IConfiguration configuration) : IPlanningChatClientFactory
{
    public async Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var server = await LlmServerConfigHelper.GetServerConfigAsync(
            llmServerConfigService,
            userSettingsService,
            model.ServerId);
        if (server is null)
            throw new InvalidOperationException($"LLM server '{model.ServerId}' was not found.");

        var apiKey = ResolveApiKey(server);
        var credential = new ApiKeyCredential(apiKey);

        OpenAIClient client;
        if (server.ServerType == ServerType.Ollama || !string.IsNullOrWhiteSpace(server.BaseUrl))
        {
            var endpoint = BuildEndpoint(server);
            client = new OpenAIClient(credential, new OpenAIClientOptions
            {
                Endpoint = endpoint
            });
        }
        else
        {
            client = new OpenAIClient(credential);
        }

        return client.GetChatClient(model.ModelName).AsIChatClient();
    }

    private Uri BuildEndpoint(LlmServerConfig server)
    {
        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl)
            ? LlmServerConfig.DefaultOllamaUrl
            : server.BaseUrl.Trim();
        if (server.ServerType == ServerType.Ollama && !baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && !baseUrl.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            baseUrl = $"{baseUrl.TrimEnd('/')}/v1";

        return new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : $"{baseUrl}/");
    }

    private string ResolveApiKey(LlmServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            return server.ApiKey;

        if (server.ServerType == ServerType.Ollama)
            return "ollama";

        var configuredApiKey = configuration["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(configuredApiKey))
            return configuredApiKey;

        throw new InvalidOperationException($"Server '{server.Name}' requires an API key.");
    }
}
